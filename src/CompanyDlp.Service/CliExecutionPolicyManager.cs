using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Xml.Linq;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Service;

// Applies the CliExecute enforcement decision for the currently active interactive console user, and
// enables the registry-backed audit prerequisites (PowerShell Script Block Logging, command-line
// process-creation auditing) that CliSensitiveCommandMonitor depends on. Mirrors BrowserPolicyManager's
// shape and gating exactly: Production + PersistentProtection only, called from DlpWorker on the same
// cadence as the browser/screen machine policies.
//
// AppLocker is NOT used anywhere in this feature, for any of the five restricted executables. This is
// a deliberate, hard-won decision - do not re-add AppLocker Exe rule collection enforcement to this
// feature. History, so nobody has to relearn this the hard way:
//
// This manager originally used a per-user AppLocker Path-Deny rule for cmd.exe/powershell.exe/
// powershell_ise.exe/pwsh.exe. That was live-confirmed to freeze the Start Menu and Windows Search for
// a Standard User on Windows 11 24H2 (build 26100) after the first reboot, because an AppLocker
// Path-Deny rule blocks every launch of the named file under the target user's token - including the
// internal calls Windows Shell itself makes to cmd.exe/powershell.exe in the background. The fix at
// that point was to move those four to a per-user Explorer DisallowRun/RestrictRun policy instead
// (Explorer-launch-only, so it never intercepts Shell's own internal calls) and keep wt.exe on a
// narrower, single-executable AppLocker Deny rule, since Windows Terminal is a separately installed app
// that Shell doesn't call into internally.
//
// That turned out not to be good enough. It was then live-confirmed - TWICE, independently - that
// simply turning ON the AppLocker Exe rule collection at all (RuleCollection Type="Exe"
// EnforcementMode="Enabled") freezes the Start Menu, Windows Search, and other unrelated apps (WhatsApp
// included) on the same Windows 11 24H2 device, regardless of what the rules inside that collection
// actually say - once with real Deny rules in effect, once with the Deny rules pointed at an inert
// placeholder SID that denies nobody. The Allow-Everyone safety-net rule being present did not help
// either time. In other words: the bug is not in rule content, targeting, or the InertSid toggle trick -
// it is enabling the Exe rule collection itself, on this device/build, at all. There is no safe way to
// use AppLocker's Exe collection for this feature on hardware like this, so it is not used for anything
// here anymore, including wt.exe.
//
// All five restricted executables (cmd.exe, powershell.exe, powershell_ise.exe, pwsh.exe, wt.exe) are
// now enforced the same way: a per-user Explorer DisallowRun + RestrictRun policy
// (HKEY_USERS\<UserSid>\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer). Explorer is both
// the only enforcer and the only caller this policy ever applies to, so it fires when the employee
// tries to open one of these five via the Start Menu, Win+R, or a double-click, and it never touches
// AppLocker, AppIDSvc, or the Exe rule collection at all. Known, accepted trade-off: unlike an
// AppLocker Path-Deny rule, DisallowRun only intercepts Explorer-initiated launches - a non-Explorer
// caller (a script directly CreateProcess-ing wt.exe, for example) is not blocked by this policy. That
// gap is deliberately accepted; it is far better than freezing the Shell for every Standard User on the
// device.
//
// SelfHealLegacyAppLockerExeEnforcement below exists because devices that ran an earlier build of this
// agent (from before this decision) can still be carrying a leftover AppLocker Exe rule collection set
// to EnforcementMode="Enabled" - confirmed live on real test devices - and nothing else on those
// devices will ever turn it back off on its own. It runs on every apply cycle to find and reset that
// automatically, with no manual intervention or reinstall required.
public sealed class CliExecutionPolicyManager(
    PolicyStore policyStore,
    PermissionEvaluator permissionEvaluator,
    AgentIdentityProvider identityProvider,
    InteractiveUserContextProvider interactiveUserContextProvider,
    AuditLogger auditLogger,
    ILogger<CliExecutionPolicyManager> logger)
{
    // Explorer DisallowRun/RestrictRun targets - all five restricted executables now, including wt.exe
    // (see class-level comment for why AppLocker is no longer used for any of them). RestrictRun's
    // value names are 1-based numeric strings, not the executable name itself, so the array index (not
    // the executable name) is what maps each entry to a fixed slot in ApplyExplorerDisallowRun -
    // re-applying only ever updates the SAME five values, never accumulates.
    private static readonly string[] ExplorerDisallowRunExecutables =
    [
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "wt.exe"
    ];

    private const string ExplorerPoliciesSubKey =
        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    // Cheap pre-check so SelfHealLegacyAppLockerExeEnforcement doesn't spawn a PowerShell process every
    // apply cycle on a device that never had the AppLocker module installed in the first place (nothing
    // to heal there - Get-AppLockerPolicy couldn't have run on a prior agent version either).
    private static readonly string AppLockerModulePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "Modules", "AppLocker", "AppLocker.psd1");

    public async Task ApplyMachinePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policy = policyStore.Get();
        if (!OperatingSystem.IsWindows()) return;

        // Runs unconditionally, ahead of every Enabled/Cli.Enabled/mode gate below - a leftover
        // AppLocker Exe rule collection from a previous agent version is dangerous (see class-level
        // comment) whether or not CLI enforcement is currently turned on for this device, and nothing
        // else will ever turn it back off on its own.
        SelfHealLegacyAppLockerExeEnforcement();

        if (!policy.Enabled || !policy.Cli.Enabled) return;
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase) || !policy.Runtime.PersistentProtection)
        {
            throw new InvalidOperationException("Machine CLI policies are only applied in Production persistent mode.");
        }

        try
        {
            EnableCommandAuditingPrerequisites();

            // "AppLocker" is a legacy config value name kept for backend/policy-JSON compatibility (see
            // DlpPolicy.CliEnforcementModes) - it no longer means "use AppLocker" and nothing below this
            // branch touches AppLocker. It means "CLI blocking is turned on"; the actual mechanism is
            // always the Explorer DisallowRun/RestrictRun policy applied below.
            if (policy.Cli.EnforcementMode.Equals(CliEnforcementModes.AppLocker, StringComparison.OrdinalIgnoreCase))
            {
                var (context, isDeniedForActiveUser) = await ResolveCliDenyDecisionAsync(policy, cancellationToken);
                ApplyExplorerDisallowRun(context, isDeniedForActiveUser);
            }

            await auditLogger.WriteAsync(new AuditEvent
            {
                EventType = "cli-policy",
                Action = "apply-machine-policy",
                Result = "success",
                Details = "CLI execution machine policy was applied."
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to apply CLI execution policy.");
            await auditLogger.WriteAsync(new AuditEvent
            {
                EventType = "cli-policy",
                Action = "apply-machine-policy",
                Result = "failed",
                Details = exception.GetType().Name
            }, cancellationToken);
            throw;
        }
    }

    // "Include command line in process creation events" (Security log 4688) and PowerShell Script
    // Block Logging (Operational log 4104) - both are prerequisites CliSensitiveCommandMonitor polls
    // for. The command-line detail is a plain Administrative Templates registry policy (same
    // SetOrDeleteDword pattern BrowserPolicyManager already uses); actually turning ON the "Process
    // Creation" audit subcategory itself is not a registry value - it lives in the Advanced Audit
    // Policy store, which only auditpol.exe can write to, so this is the one place this feature shells
    // out to a system tool (same narrowly-scoped pattern UsbDeviceController already uses for
    // pnputil.exe - a fixed, absolute-pathed, non-cmd/non-PowerShell executable).
    private void EnableCommandAuditingPrerequisites()
    {
        using (var scriptBlockLoggingKey = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Policies\Microsoft\Windows\PowerShell\ScriptBlockLogging", true))
        {
            scriptBlockLoggingKey?.SetValue("EnableScriptBlockLogging", 1, RegistryValueKind.DWord);
        }

        using (var auditPolicyKey = Registry.LocalMachine.CreateSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit", true))
        {
            auditPolicyKey?.SetValue("ProcessCreationIncludeCmdLine_Enabled", 1, RegistryValueKind.DWord);
        }

        RunSystemToolFireAndForget(
            Path.Combine(Environment.SystemDirectory, "auditpol.exe"),
            "/set /subcategory:\"Process Creation\" /success:enable");
    }

    // Finds and disarms a leftover AppLocker Exe rule collection from a previous agent version. See the
    // class-level comment for why: enabling RuleCollection Type="Exe" EnforcementMode="Enabled" AT ALL
    // - even with harmless/inert rule content - was live-confirmed twice to freeze Start Menu, Windows
    // Search, and other apps on affected devices. This agent no longer ever enables that collection
    // itself, but a device that ran an older build before this fix may still carry it, and nothing else
    // will ever turn it back off. Runs every apply cycle (not just once) so it heals the device no
    // matter when this version first reaches it, and keeps healing it if anything else ever re-enables
    // it. Read-only unless a problem is actually found - one PowerShell round trip in the common
    // (already-healthy) case, a second one only when there is something to actually fix.
    private void SelfHealLegacyAppLockerExeEnforcement()
    {
        if (!File.Exists(AppLockerModulePath)) return;

        try
        {
            var currentPolicyXml = RunPowerShellCaptureStdout(
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"(Get-AppLockerPolicy -Local -Xml)\"",
                timeoutSeconds: 20);
            if (string.IsNullOrWhiteSpace(currentPolicyXml)) return;

            var exeCollection = XDocument.Parse(currentPolicyXml)
                .Descendants("RuleCollection")
                .FirstOrDefault(element => (string?)element.Attribute("Type") == "Exe");
            if (exeCollection is null) return;
            if (!string.Equals((string?)exeCollection.Attribute("EnforcementMode"), "Enabled", StringComparison.OrdinalIgnoreCase))
                return;

            logger.LogWarning(
                "Found a leftover AppLocker Exe rule collection with EnforcementMode=\"Enabled\" on this " +
                "device, left behind by a previous agent version. This is known to freeze Start Menu/" +
                "Windows Search on Windows 11 24H2 (build 26100) regardless of what the collection's " +
                "rules say. Resetting it to NotConfigured now.");

            var resetXmlPath = Path.Combine(Path.GetTempPath(), $"CompanyDlp-AppLockerSelfHeal-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(resetXmlPath, """
                    <AppLockerPolicy Version="1">
                      <RuleCollection Type="Exe" EnforcementMode="NotConfigured" />
                    </AppLockerPolicy>
                    """);

                // No -Merge: a deliberate full replacement of the Exe collection, wiping every rule in
                // it (this agent's old ones and anyone else's) along with EnforcementMode itself.
                // -Merge can only add/update rules by Id - it can never remove one or turn
                // EnforcementMode back to NotConfigured, so it cannot undo the exact problem being
                // healed here.
                var powerShellPath = Path.Combine(
                    Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
                RunSystemToolFireAndForget(
                    powerShellPath,
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
                    $"\"Set-AppLockerPolicy -XmlPolicy '{resetXmlPath}'\"",
                    timeoutSeconds: 30);
            }
            finally
            {
                try { File.Delete(resetXmlPath); } catch { /* best effort cleanup of a temp file */ }
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "AppLocker Exe-collection self-heal check failed; will retry next cycle.");
        }
    }

    // Evaluates the CliExecute decision for the active console user and applies the local-administrator
    // exemption, for ApplyExplorerDisallowRun to act on.
    private async Task<(ClientContext Context, bool IsDeniedForActiveUser)> ResolveCliDenyDecisionAsync(
        DlpPolicy policy, CancellationToken cancellationToken)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        if (string.IsNullOrWhiteSpace(context.UserSid))
            return (context, false);

        var decision = permissionEvaluator.Evaluate(
            policy, ActionKeys.CliExecute, context, identityProvider.Get(), DateTimeOffset.UtcNow);
        if (decision.IsAllowed)
            return (context, false);

        // Local admins keep cmd.exe/PowerShell/wt.exe access even when the employee-facing policy for
        // this device is Deny - an admin locked out of a shell on their own machine (including via a
        // stale/incorrectly-scoped grant) has no realistic self-service recovery path, and Explorer
        // DisallowRun gives no separate "except elevated sessions" option of its own. Checked by live
        // token/group membership (WindowsPrincipal), not a hardcoded SID, so it still applies correctly
        // through domain group membership changes.
        if (IsLocalAdministrator(context.WindowsSessionId))
        {
            logger.LogInformation(
                "CLI execution Deny policy was evaluated for {Username} ({UserSid}) but not applied because the account is a local administrator.",
                context.Username, context.UserSid);

            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = ActionKeys.CliExecute,
                EventType = "CliEnforcementAdminExemption",
                Action = "cli-policy-admin-exemption",
                Method = nameof(ResolveCliDenyDecisionAsync),
                Result = "exempted",
                ReasonCode = "LocalAdministratorExempt",
                UserSid = context.UserSid,
                Username = context.Username,
                WindowsSessionId = context.WindowsSessionId,
                Details = "CLI execution Deny policy evaluated to Deny for this user but was not applied because the account is a member of BUILTIN\\Administrators.",
            }, cancellationToken);

            return (context, false);
        }

        return (context, true);
    }

    // Writes/clears the Explorer DisallowRun + RestrictRun policy directly in the active console user's
    // own registry hive (HKEY_USERS\<UserSid>\...\Policies\Explorer) - the SAME idempotent set-or-delete
    // shape BrowserPolicyManager.SetOrDeleteDword already uses (set when the policy should apply,
    // delete - never leave a stale "0" - when it shouldn't).
    //
    // Their hive is only mounted under HKEY_USERS while they are the active interactive console user,
    // i.e. exactly when `context` here was captured. Once written, the values live in NTUSER.DAT and
    // persist across logoff/logon on their own - Explorer re-reads and re-enforces them at every logon
    // without this service needing to be running.
    private void ApplyExplorerDisallowRun(ClientContext context, bool shouldDeny)
    {
        if (string.IsNullOrWhiteSpace(context.UserSid)) return;

        using var userHive = Registry.Users.OpenSubKey(context.UserSid, writable: true);
        if (userHive is null)
        {
            // Not an error worth failing the whole apply cycle over - it just means the active console
            // user's profile hive isn't currently mounted under HKEY_USERS (e.g. a brief window around
            // logon/logoff). The next ~15s apply cycle picks it up once it is.
            logger.LogDebug(
                "Could not open the registry hive for {UserSid} under HKEY_USERS; Explorer CLI restriction was not applied this cycle.",
                context.UserSid);
            return;
        }

        using var explorerKey = userHive.CreateSubKey(ExplorerPoliciesSubKey, true);
        if (explorerKey is null) return;

        SetOrDeleteDword(explorerKey, "DisallowRun", shouldDeny, 1);

        if (shouldDeny)
        {
            using var restrictRunKey = explorerKey.CreateSubKey("RestrictRun", true);
            for (var i = 0; i < ExplorerDisallowRunExecutables.Length; i++)
                restrictRunKey?.SetValue((i + 1).ToString(), ExplorerDisallowRunExecutables[i], RegistryValueKind.String);
        }
        else
        {
            explorerKey.DeleteSubKeyTree("RestrictRun", throwOnMissingSubKey: false);
        }
    }

    private static void SetOrDeleteDword(RegistryKey key, string name, bool enabled, int enabledValue)
    {
        if (enabled) key.SetValue(name, enabledValue, RegistryValueKind.DWord);
        else key.DeleteValue(name, false);
    }

    // Queries a live token for the interactive console session rather than comparing against a
    // hardcoded SID/name, so this reflects actual effective membership in BUILTIN\Administrators -
    // including via nested domain group membership - the same way Windows itself would evaluate it.
    // Deliberately conservative: any failure (no session, query failure, disposed identity) returns
    // false so a broken admin-detection path fails toward "Deny still applies" rather than silently
    // exempting an account that was never actually confirmed to be an administrator.
    private static bool IsLocalAdministrator(int sessionId)
    {
        if (!OperatingSystem.IsWindows() || sessionId <= 0) return false;

        if (!WTSQueryUserToken((uint)sessionId, out var token))
            return false;

        try
        {
            using var identity = new WindowsIdentity(token);
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // Same pipe-draining shape as RunSystemToolFireAndForget below (avoids the exact OS-pipe-buffer
    // deadlock that method's own comment documents), but returns stdout to the caller instead of only
    // logging it. Used only by SelfHealLegacyAppLockerExeEnforcement, which needs to actually read
    // Get-AppLockerPolicy's output, not just know whether the command succeeded.
    private string? RunPowerShellCaptureStdout(string arguments, int timeoutSeconds)
    {
        var powerShellPath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powerShellPath)) return null;

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = powerShellPath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process is null) return null;

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(timeoutSeconds * 1000);
        var stdOut = stdOutTask.GetAwaiter().GetResult();
        var stdErr = stdErrTask.GetAwaiter().GetResult().Trim();

        if (!exited)
        {
            logger.LogDebug("PowerShell query {Arguments} did not exit within {TimeoutSeconds}s.", arguments, timeoutSeconds);
            return null;
        }

        if (process.ExitCode != 0)
        {
            logger.LogDebug(
                "PowerShell query {Arguments} exited with code {ExitCode}. Stderr: {Stderr}",
                arguments, process.ExitCode, stdErr);
            return null;
        }

        return stdOut;
    }

    // Every caller of this (auditpol.exe, Set-AppLockerPolicy) is a real, meaningful system change
    // that fails silently and dangerously if nobody reads the child process's own outcome - the
    // original fire-and-forget shape (start, WaitForExit, discard) is exactly what let a real
    // Access-Denied failure from a non-elevated Service process vanish without a trace during this
    // feature's own testing. Read stdout/stderr and check the exit code every time, and log the
    // outcome - including on success - so "did this actually apply" is never a silent unknown again.
    private void RunSystemToolFireAndForget(string fileName, string arguments, int timeoutSeconds = 15)
    {
        if (!File.Exists(fileName))
        {
            logger.LogError("Could not run system tool because it was not found: {FileName}", fileName);
            return;
        }

        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process is null)
        {
            logger.LogError("Failed to start system tool {FileName} {Arguments}.", fileName, arguments);
            return;
        }

        // Reads must start before the blocking WaitForExit below - otherwise a child process that
        // writes enough output to fill the OS pipe buffer would deadlock (it blocks writing while
        // this process blocks waiting for exit, and neither side is draining the pipe).
        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(timeoutSeconds * 1000);
        var stdOut = stdOutTask.GetAwaiter().GetResult().Trim();
        var stdErr = stdErrTask.GetAwaiter().GetResult().Trim();

        if (!exited)
        {
            logger.LogError(
                "System tool {FileName} {Arguments} did not exit within {TimeoutSeconds}s. Stdout: {Stdout} Stderr: {Stderr}",
                fileName, arguments, timeoutSeconds, stdOut, stdErr);
            return;
        }

        if (process.ExitCode != 0)
        {
            logger.LogError(
                "System tool {FileName} {Arguments} exited with code {ExitCode}. Stdout: {Stdout} Stderr: {Stderr}",
                fileName, arguments, process.ExitCode, stdOut, stdErr);
        }
        else
        {
            logger.LogInformation(
                "System tool {FileName} {Arguments} completed successfully (exit code 0). Stdout: {Stdout} Stderr: {Stderr}",
                fileName, arguments, stdOut, stdErr);
        }
    }
}
