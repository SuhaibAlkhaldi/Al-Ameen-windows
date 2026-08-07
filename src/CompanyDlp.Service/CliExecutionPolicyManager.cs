using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Service;

// Applies the CliExecute enforcement decision as a real, per-user AppLocker Deny rule for the
// currently active interactive console user, and enables the registry-backed audit prerequisites
// (PowerShell Script Block Logging, command-line process-creation auditing) that
// CliSensitiveCommandMonitor depends on. Mirrors BrowserPolicyManager's shape and gating exactly:
// Production + PersistentProtection only, called from DlpWorker on the same cadence as the
// browser/screen machine policies.
//
// AppLocker over WDAC (see DlpPolicy.CliEnforcementModes): WDAC policy applies device-wide with no
// native per-user/per-group concept - a blanket WDAC deny on cmd.exe/powershell.exe would also catch
// SYSTEM-context scheduled tasks and any other legitimate script on the device, with no way to scope
// it to just the employee this policy actually targets. AppLocker natively supports per-user/group
// Deny rules keyed by SID, which matches this product's actual policy model (deny specific
// employees, not the whole device) far more closely than WDAC does.
//
// Path rules, not Publisher rules (known limitation, documented honestly): Publisher rule conditions
// would survive a copy/rename of the target binary far better than Path rules do, but they require
// the exact Authenticode certificate subject string for each executable - not something that can be
// safely hardcoded here without a live signed binary to inspect via Get-AppLockerFileInformation. A
// wrong hardcoded certificate subject would make the rule silently never match anything, which is a
// worse failure mode than a documented, honest Path-rule limitation. v1 uses Path rules; a follow-up
// can move to Publisher rules once the exact certificate subjects are confirmed on a real device.
public sealed class CliExecutionPolicyManager(
    PolicyStore policyStore,
    PermissionEvaluator permissionEvaluator,
    AgentIdentityProvider identityProvider,
    InteractiveUserContextProvider interactiveUserContextProvider,
    CliEnforcementHealthChecker healthChecker,
    AuditLogger auditLogger,
    ILogger<CliExecutionPolicyManager> logger)
{
    // Only re-report the health status to the backend on a transition (including the very first
    // check), not on every ~15s apply cycle - a device stuck ServiceNotRunning would otherwise raise
    // a fresh High alert every cycle forever, drowning out everything else in the Alerts list.
    private CliEnforcementHealthStatus? _lastReportedStatus;

    // Fixed rule/collection ids so every apply cycle updates the SAME rules via "-Merge" instead of
    // accumulating duplicates, and so this manager never touches AppLocker rules an admin authored
    // by hand for anything else. The "Allow Everyone: *" rule is the standard AppLocker safety net -
    // without at least one Allow rule present, turning EnforcementMode="Enabled" on for a rule
    // collection blocks EVERY executable in that collection for EVERYONE, not just the four names
    // this feature cares about. Never ship the Deny rules below without this rule alongside them.
    private const string AllowEveryoneRuleId = "8f6a9b1e-2c3d-4e5f-9a1b-0c2d3e4f5a61";

    // "Null SID" - a well-known Windows security identifier that can never be a real logon's SID, so
    // pointing the Deny rule's UserOrGroupSid at it is equivalent to "this rule denies no one" without
    // ever removing the rule from the local AppLocker store (Set-AppLockerPolicy -Merge only adds/
    // updates rules by Id; it cannot remove one). Flipping between a real employee SID and this
    // placeholder is how this manager grants and revokes enforcement idempotently through -Merge alone.
    private const string InertSid = "S-1-0-0";

    private static readonly (string RuleId, string ExecutableName, string PathCondition)[] RestrictedExecutables =
    [
        ("a1b1c1d1-0001-4000-8000-000000000001", "cmd.exe", @"%SYSTEM32%\cmd.exe"),
        ("a1b1c1d1-0001-4000-8000-000000000002", "powershell.exe", @"%SYSTEM32%\WindowsPowerShell\v1.0\powershell.exe"),
        ("a1b1c1d1-0001-4000-8000-000000000003", "powershell_ise.exe", @"%SYSTEM32%\WindowsPowerShell\v1.0\powershell_ise.exe"),
        ("a1b1c1d1-0001-4000-8000-000000000004", "pwsh.exe", @"%PROGRAMFILES%\PowerShell\7\pwsh.exe"),
    ];

    public async Task ApplyMachinePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policy = policyStore.Get();
        if (!OperatingSystem.IsWindows()) return;
        if (!policy.Enabled || !policy.Cli.Enabled) return;
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase) || !policy.Runtime.PersistentProtection)
        {
            throw new InvalidOperationException("Machine CLI policies are only applied in Production persistent mode.");
        }

        try
        {
            EnableCommandAuditingPrerequisites();

            if (policy.Cli.EnforcementMode.Equals(CliEnforcementModes.AppLocker, StringComparison.OrdinalIgnoreCase))
            {
                var health = healthChecker.Check();
                await ReportHealthTransitionAsync(health, cancellationToken);

                // Still write/stage the AppLocker rule even when unhealthy (harmless, and means
                // enforcement starts immediately the moment AppIDSvc is started or the device is
                // reimaged onto a supported edition) - the point of the health check is to make sure
                // this is never mistaken for actual enforcement, not to skip writing the rule.
                await ApplyAppLockerDenyRuleAsync(policy, cancellationToken);
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

    // EventType is what AgentAuditEventService keys its independent (regardless-of-Decision) alert
    // branch on for this, same "independent" pattern as the CliSensitiveCommand and integrity-mismatch
    // branches - a High alert whenever enforcement isn't genuinely active is exactly the point of this
    // whole health check, so it must not depend on some other Decision happening to also be "Block".
    private async Task ReportHealthTransitionAsync(CliEnforcementHealth health, CancellationToken cancellationToken)
    {
        if (_lastReportedStatus == health.Status) return;
        var previousStatus = _lastReportedStatus;
        _lastReportedStatus = health.Status;

        var isActive = health.Status == CliEnforcementHealthStatus.Active;
        var reasonCode = health.Status switch
        {
            CliEnforcementHealthStatus.Active => "CliEnforcementActive",
            CliEnforcementHealthStatus.NotSupportedPlatform => "CliEnforcementNotSupportedOnPlatform",
            CliEnforcementHealthStatus.ModuleUnavailable => "CliEnforcementModuleUnavailable",
            CliEnforcementHealthStatus.ServiceNotRunning => "CliEnforcementServiceNotRunning",
            _ => "CliEnforcementUnknown"
        };

        if (!isActive)
        {
            logger.LogWarning(
                "CLI execution enforcement is not active on this device (status={Status}, edition={Edition}). AppLocker Deny rules will be written but will not actually block anything until this is resolved.",
                health.Status, health.EditionDisplayName);
        }

        await auditLogger.WriteAsync(new AuditEvent
        {
            ActionKey = ActionKeys.CliExecute,

            // Deliberately a different EventType when restored to Active: AgentAuditEventService's
            // independent alert branch fires on EventType=="CliEnforcementUnavailable" alone (no
            // Decision/Result inspection needed), so the good-news transition must never share that
            // EventType or every device that starts up healthy would raise a spurious alert.
            EventType = isActive ? "CliEnforcementRestored" : "CliEnforcementUnavailable",
            Action = "cli-enforcement-health-check",
            Method = "CliEnforcementHealthChecker",
            Result = isActive ? "active" : "not-enforced",
            ReasonCode = reasonCode,
            Details = $"edition={health.EditionDisplayName} ({health.EditionId}); previousStatus={previousStatus?.ToString() ?? "none"}; currentStatus={health.Status}",
        }, cancellationToken);
    }

    private async Task ApplyAppLockerDenyRuleAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var targetSid = InertSid;

        if (!string.IsNullOrWhiteSpace(context.UserSid))
        {
            var decision = permissionEvaluator.Evaluate(
                policy, ActionKeys.CliExecute, context, identityProvider.Get(), DateTimeOffset.UtcNow);
            if (!decision.IsAllowed)
            {
                // Local admins keep cmd.exe/PowerShell access even when the employee-facing policy for
                // this device is Deny - an admin locked out of a shell on their own machine (including
                // via a stale/incorrectly-scoped grant) has no realistic self-service recovery path, and
                // AppLocker Deny rules give no separate "except elevated sessions" option of their own.
                // Checked by live token/group membership (WindowsPrincipal), not a hardcoded SID, so it
                // still applies correctly through domain group membership changes.
                if (IsLocalAdministrator(context.WindowsSessionId))
                {
                    logger.LogInformation(
                        "CLI execution Deny rule was evaluated for {Username} ({UserSid}) but not applied because the account is a local administrator.",
                        context.Username, context.UserSid);

                    await auditLogger.WriteAsync(new AuditEvent
                    {
                        ActionKey = ActionKeys.CliExecute,
                        EventType = "CliEnforcementAdminExemption",
                        Action = "cli-policy-admin-exemption",
                        Method = nameof(ApplyAppLockerDenyRuleAsync),
                        Result = "exempted",
                        ReasonCode = "LocalAdministratorExempt",
                        UserSid = context.UserSid,
                        Username = context.Username,
                        WindowsSessionId = context.WindowsSessionId,
                        Details = "CLI execution Deny policy evaluated to Deny for this user but was not applied because the account is a member of BUILTIN\\Administrators.",
                    }, cancellationToken);
                }
                else
                {
                    targetSid = context.UserSid;
                }
            }
        }

        var policyXmlPath = Path.Combine(Path.GetTempPath(), $"CompanyDlp-CliExecute-{Guid.NewGuid():N}.xml");
        try
        {
            await File.WriteAllTextAsync(policyXmlPath, BuildAppLockerPolicyXml(targetSid), cancellationToken);
            RunPowerShellSetAppLockerPolicy(policyXmlPath);
        }
        finally
        {
            try { File.Delete(policyXmlPath); } catch { /* best effort cleanup of a temp file */ }
        }
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

    // Standard AppLocker local-policy XML shape (the same document Set-AppLockerPolicy -XmlPolicy
    // expects) - one Allow-Everyone default rule plus one Path Deny rule per restricted executable,
    // all pointed at the SAME fixed rule ids every cycle so re-applying only ever updates these four
    // rules via -Merge, never duplicates them.
    private static string BuildAppLockerPolicyXml(string denySid)
    {
        var denyRules = string.Join(Environment.NewLine, RestrictedExecutables.Select(exe => $"""
              <FilePathRule Id="{exe.RuleId}" Name="CompanyDlp deny {SecurityElement.Escape(exe.ExecutableName)}" Description="Managed by Company DLP (CliExecute policy)" UserOrGroupSid="{denySid}" Action="Deny">
                <Conditions>
                  <FilePathCondition Path="{SecurityElement.Escape(exe.PathCondition)}" />
                </Conditions>
              </FilePathRule>
        """));

        return $"""
        <AppLockerPolicy Version="1">
          <RuleCollection Type="Exe" EnforcementMode="Enabled">
            <FilePathRule Id="{AllowEveryoneRuleId}" Name="(Default Rule) All files" Description="Allows members of the Everyone group to run applications. Required safety net so enabling this collection does not block every executable." UserOrGroupSid="S-1-1-0" Action="Allow">
              <Conditions>
                <FilePathCondition Path="*" />
              </Conditions>
            </FilePathRule>
        {denyRules}
          </RuleCollection>
        </AppLockerPolicy>
        """;
    }

    private void RunPowerShellSetAppLockerPolicy(string policyXmlPath)
    {
        var powerShellPath = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        // Runs under this Windows Service's own SYSTEM/service account, never the interactive
        // employee session - confirmed clean (grepped this whole agent codebase) that nothing else
        // here ever shells out to cmd.exe/powershell.exe, so this is the one intentional exception.
        // It is not, and cannot be, blocked by the very Deny rule it is applying: that rule's
        // UserOrGroupSid only ever targets a specific employee SID (or the inert placeholder above),
        // never SYSTEM.
        RunSystemToolFireAndForget(
            powerShellPath,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command " +
            $"\"Set-AppLockerPolicy -XmlPolicy '{policyXmlPath}' -Merge\"",
            timeoutSeconds: 30);
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
