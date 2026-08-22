using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Service;

public sealed class BrowserPolicyManager(
    PolicyStore policyStore,
    PermissionEvaluator permissionEvaluator,
    AgentIdentityProvider identityProvider,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ExtensionHealthChecker extensionHealthChecker,
    AuditLogger auditLogger,
    ILogger<BrowserPolicyManager> logger)
{
    private const string EdgePath = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ChromePath = @"SOFTWARE\Policies\Google\Chrome";
    private const string FirefoxPath = @"SOFTWARE\Policies\Mozilla\Firefox";

    // Chrome/Edge convert a list-type policy's registry subkey (ExtensionInstallForcelist/Blocklist)
    // into a JSON array only when the subkey's value names are exactly "1".."N" for however many
    // values exist (RegistryDict::ToValue() in Chromium's components/policy/core/common/
    // policy_loader_win.cc) - any other naming, including the fixed "9999" this file used before,
    // makes Chromium treat the subkey as an object instead of a list. A list-typed policy schema
    // (which ExtensionInstallForcelist is) rejects an object value outright, so the entry is silently
    // discarded - no request is ever issued, no error is logged, nothing happens.
    // scripts/register-browser-force-install.ps1 already used "1" and separately documented reaching
    // this same conclusion; this file's "9999" was the one remaining place still doing it wrong -
    // single source of truth from here on.
    // NOT independently confirmed live against a real browser in this change: this session had no
    // elevated/administrator access to write HKLM policy keys (verified by attempting it and getting
    // "Requested registry access is not allowed") and so could not observe chrome://policy/edge://policy
    // directly. This is decided from Chromium's documented registry-policy-loading behavior instead -
    // confirm on a real admin-capable test machine before treating it as settled.
    // internal (not private) solely so CompanyDlp.Tests can assert against the actual constant instead
    // of a hardcoded string duplicate that could silently drift out of sync with it.
    internal const string ExtensionPolicyValueName = "1";

    // Per-condition dedup so a persistent problem (misconfigured id/URL, or an extension missing from
    // the active user's profile) is logged once when it starts and once when it clears, not every
    // ~PolicyReapplySeconds cycle - the exact "1177+ fabricated audit rows for one device" flooding
    // this same method already caused once before (see ApplyMachinePoliciesAsync's ActionKey comment)
    // is the failure mode this guards against. Safe as instance state because this class is registered
    // AddSingleton (Program.cs) - one instance for the process lifetime, one worker loop calling it.
    private readonly HashSet<string> _activeExtensionAlerts = new(StringComparer.OrdinalIgnoreCase);

    public async Task ApplyMachinePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policy = policyStore.Get();
        if (!OperatingSystem.IsWindows()) return;
        if (!policy.Enabled || (!policy.Browser.Enabled && !policy.Screen.Enabled)) return;
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase) || !policy.Runtime.PersistentProtection)
        {
            throw new InvalidOperationException("Machine browser policies are only applied in Production persistent mode.");
        }

        List<ExtensionPolicyResult> extensionResults = [];

        try
        {
            if (policy.Browser.Enabled)
            {
                // policyStore.Get() is the raw/base policy shared machine-wide — it carries no per-employee
                // grant data (the backend never sends a Browser section at all). browser.download is a
                // per-subject grantable permission, so resolve it against the active console user's grants
                // (same DeviceId-scoped matching UsbProtectionMonitor already uses) instead of blindly
                // applying the static default.
                var blockDownloads = ResolveBlockDownloads(policy);
                extensionResults.Add(ApplyEdge(policy.Browser, blockDownloads));
                extensionResults.Add(ApplyChrome(policy.Browser, blockDownloads));
                extensionResults.Add(ApplyFirefox(policy.Browser));
            }
            ApplyWindowsScreenCapturePolicy(policy.Screen, ResolveDisableGameCapture(policy));
            // ActionKey is set explicitly here (as ActionKeys.PolicyApply, the existing internal/
            // audit-only channel) - without it, SecurityEventFactory.ResolveActionKey falls back to its
            // EventType-substring heuristic, and because EventType="browser-policy" contains "browser",
            // every single one of these purely internal, no-user-action, ~15-20s-cadence housekeeping
            // events was silently mapped to ActionKeys.BrowserUpload (the browser branch's catch-all
            // default) and showed up in the portal as fabricated "Browser Upload / Allow" audit rows -
            // confirmed live 2026-08-17: 1177+ such rows for a single device, drowning out any real
            // browser.upload activity and making that audit view untrustworthy. policy.apply is already
            // a seeded, valid ActionKey (used by the real remote-policy-apply events), so this requires
            // no backend/DB change.
            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = ActionKeys.PolicyApply,
                EventType = "browser-policy",
                Action = "apply-machine-policy",
                Result = "success",
                Details = policy.Browser.Enabled
                    ? $"Browser and Windows screen-capture machine policies were applied. Extensions: {DescribeExtensionResults(extensionResults)}."
                    : "Browser and Windows screen-capture machine policies were applied."
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to apply browser policies.");
            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = ActionKeys.PolicyApply,
                EventType = "browser-policy",
                Action = "apply-machine-policy",
                Result = "failed",
                Details = exception.GetType().Name
            }, cancellationToken);
            throw;
        }

        // Deliberately outside the try/catch above: a misconfigured extension id/URL or a health-check
        // failure is not a policy-apply failure (the registry writes above genuinely succeeded) and must
        // never be reported or retried as one.
        await LogExtensionValidationAlertsAsync(extensionResults, cancellationToken);
        await RunExtensionHealthChecksAsync(extensionResults, cancellationToken);
    }

    private bool ResolveBlockDownloads(DlpPolicy policy)
    {
        if (!policy.Browser.BlockDownloads) return false;
        return ShouldBlockForMissingGrant(policy, permissionEvaluator, ActionKeys.BrowserDownload,
            interactiveUserContextProvider.GetActiveConsoleUser(), identityProvider.Get());
    }

    // Mirrors ResolveBlockDownloads: DisableWindowsGameCapture is part of the per-subject-grantable
    // ScreenRecording permission (see EffectivePolicyBuilder, which already relaxes this flag when the
    // grant allows it), but this machine-wide registry policy is the only Production-mode enforcement
    // point for it — without this check a ScreenRecording grant would relax the cached flag the Desktop
    // app reads while leaving Game Bar/GameDVR recording blocked at the OS level regardless.
    private bool ResolveDisableGameCapture(DlpPolicy policy)
    {
        if (!policy.Screen.Enabled || !policy.Screen.DisableWindowsGameCapture) return false;
        return ShouldBlockForMissingGrant(policy, permissionEvaluator, ActionKeys.ScreenRecording,
            interactiveUserContextProvider.GetActiveConsoleUser(), identityProvider.Get());
    }

    // Internal (not private) and static so it's directly unit-testable without constructing the full
    // BrowserPolicyManager dependency graph (PolicyStore/AuditLogger/etc. touch real files and DPAPI).
    internal static bool ShouldBlockForMissingGrant(
        DlpPolicy policy, PermissionEvaluator evaluator, string actionKey, ClientContext context, AgentIdentity identity)
        => !evaluator.Evaluate(policy, actionKey, context, identity, DateTimeOffset.UtcNow).IsAllowed;

    private static void ApplyWindowsScreenCapturePolicy(ScreenPolicy policy, bool disableGameCapture)
    {
        const string gameDvrPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
        using var key = Registry.LocalMachine.CreateSubKey(gameDvrPath, true)
            ?? throw new InvalidOperationException("Could not open the Windows GameDVR policy registry key.");

        SetOrDeleteDword(key, "AllowGameDVR", policy.Enabled && disableGameCapture, 0);
    }

    private static ExtensionPolicyResult ApplyEdge(BrowserPolicy policy, bool blockDownloads)
    {
        using var key = Registry.LocalMachine.CreateSubKey(EdgePath, true)
            ?? throw new InvalidOperationException("Could not open the Edge policy registry key.");

        SetOrDeleteDword(key, "InPrivateModeAvailability", policy.DisableIncognito, 1);
        SetOrDeleteDword(key, "BrowserGuestModeEnabled", policy.DisableGuestMode, 0);
        SetOrDeleteDword(key, "DisableScreenshots", policy.DisableBrowserScreenshots, 1);
        SetOrDeleteDword(key, "WebCaptureEnabled", policy.DisableBrowserScreenshots, 0);
        SetOrDeleteDword(key, "DownloadRestrictions", blockDownloads, 3);

        return ApplyExtensionPolicy(Registry.LocalMachine, EdgePath, policy.EdgeExtensionId, policy.EdgeExtensionUpdateUrl,
            policy.BlockUnapprovedExtensions, ExtensionPlatform.Edge);
    }

    private static ExtensionPolicyResult ApplyChrome(BrowserPolicy policy, bool blockDownloads)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ChromePath, true)
            ?? throw new InvalidOperationException("Could not open the Chrome policy registry key.");

        SetOrDeleteDword(key, "IncognitoModeAvailability", policy.DisableIncognito, 1);
        SetOrDeleteDword(key, "BrowserGuestModeEnabled", policy.DisableGuestMode, 0);
        SetOrDeleteDword(key, "DownloadRestrictions", blockDownloads, 3);

        return ApplyExtensionPolicy(Registry.LocalMachine, ChromePath, policy.ChromeExtensionId, policy.ChromeExtensionUpdateUrl,
            policy.BlockUnapprovedExtensions, ExtensionPlatform.Chrome);
    }

    // Firefox has no equivalent of Chrome/Edge's Incognito/GuestMode/Screenshot/DownloadRestrictions
    // registry policies applied here - those would need their own separate research into Firefox's
    // (different) enterprise policy names and are out of scope for this fix, which targets specifically
    // the "every extension blocked, including Ameen's own, with no working alternative" failure mode.
    // Firefox parity here is for extension force-install/block only.
    private static ExtensionPolicyResult ApplyFirefox(BrowserPolicy policy)
    {
        var status = ExtensionPolicyValidator.Validate(policy.FirefoxExtensionId, policy.FirefoxExtensionUpdateUrl, ExtensionPlatform.Firefox);
        var plan = BuildWritePlan(status, policy.BlockUnapprovedExtensions);

        using var key = Registry.LocalMachine.CreateSubKey($@"{FirefoxPath}\ExtensionSettings", true)
            ?? throw new InvalidOperationException("Could not open the Firefox ExtensionSettings policy registry key.");

        // Firefox's ExtensionSettings registry mapping (documented by Mozilla's own policy-templates
        // project, mozilla/policy-templates) is one VALUE PER EXTENSION - the value NAME is the
        // extension id itself (or "*" for the catch-all rule), and the value DATA is a JSON string
        // for just that one entry. This is structurally different from Chrome/Edge's numbered-list-of-
        // strings convention above (ExtensionPolicyValueName does not apply here at all).
        // NOT independently confirmed live in this change - see ExtensionPolicyValueName's comment for
        // why (no elevated access this session); confirm on a real admin-capable Windows+Firefox test
        // machine before production use.
        if (plan.WriteForcelist)
        {
            var entryJson = JsonSerializer.Serialize(new
            {
                installation_mode = "force_installed",
                install_url = policy.FirefoxExtensionUpdateUrl
            });
            key.SetValue(policy.FirefoxExtensionId, entryJson, RegistryValueKind.String);
        }
        // else: nothing to clean up by a fixed name here, since the value name IS the extension id.
        // Known limitation: if the id itself changes between policy syncs (it's derived from the
        // signing key, so this should be rare), the previous id's registry value is orphaned rather
        // than removed automatically.

        if (plan.WriteBlocklist)
        {
            key.SetValue("*", """{"installation_mode":"blocked"}""", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("*", false);
        }

        return new ExtensionPolicyResult(ExtensionPlatform.Firefox, status, policy.FirefoxExtensionId ?? "", policy.FirefoxExtensionUpdateUrl ?? "", plan.WriteBlocklist);
    }

    private static ExtensionPolicyResult ApplyExtensionPolicy(
        RegistryKey hive, string browserPath, string extensionId, string updateUrl, bool blockOthers, ExtensionPlatform platform)
    {
        var status = ExtensionPolicyValidator.Validate(extensionId, updateUrl, platform);
        var plan = BuildWritePlan(status, blockOthers);
        var forcelistValue = plan.WriteForcelist ? $"{extensionId};{updateUrl}" : null;

        using (var forceList = hive.CreateSubKey($@"{browserPath}\ExtensionInstallForcelist", true))
        {
            if (plan.WriteForcelist) forceList?.SetValue(ExtensionPolicyValueName, forcelistValue!, RegistryValueKind.String);
            else forceList?.DeleteValue(ExtensionPolicyValueName, false);
        }

        using (var blockList = hive.CreateSubKey($@"{browserPath}\ExtensionInstallBlocklist", true))
        {
            if (plan.WriteBlocklist) blockList?.SetValue(ExtensionPolicyValueName, "*", RegistryValueKind.String);
            else blockList?.DeleteValue(ExtensionPolicyValueName, false);
        }

        return new ExtensionPolicyResult(platform, status, extensionId ?? "", updateUrl ?? "", plan.WriteBlocklist);
    }

    // Pure decision logic, deliberately separated from the actual RegistryKey.SetValue/DeleteValue
    // calls above so it's unit-testable without any registry access at all (this process had none in
    // this session - see ExtensionPolicyValueName's comment). This is the one place that answers
    // "given what we know about the id/URL, is it safe to (a) force-install and (b) block everything
    // else" - and (b) is only ever true when (a) actually happened. That link is the entire fix for the
    // "every extension blocked, including Ameen's own" incident: the old code wrote
    // ExtensionInstallBlocklist="*" completely unconditionally, regardless of whether the forcelist
    // entry it wrote alongside it was even valid.
    internal readonly record struct RegistryExtensionWritePlan(bool WriteForcelist, bool WriteBlocklist);

    internal static RegistryExtensionWritePlan BuildWritePlan(ExtensionForceInstallStatus status, bool blockOthersRequested) =>
        status == ExtensionForceInstallStatus.Valid
            ? new RegistryExtensionWritePlan(WriteForcelist: true, WriteBlocklist: blockOthersRequested)
            : new RegistryExtensionWritePlan(WriteForcelist: false, WriteBlocklist: false);

    internal readonly record struct ExtensionPolicyResult(
        ExtensionPlatform Platform, ExtensionForceInstallStatus Status, string ExtensionId, string UpdateUrl, bool BlockedOthers);

    private static string DescribeExtensionResults(IEnumerable<ExtensionPolicyResult> results) =>
        string.Join("; ", results.Select(r => r.Status == ExtensionForceInstallStatus.Valid
            ? $"{r.Platform}=Valid(id={r.ExtensionId})"
            : $"{r.Platform}={r.Status}"));

    private async Task LogExtensionValidationAlertsAsync(List<ExtensionPolicyResult> results, CancellationToken cancellationToken)
    {
        foreach (var result in results)
        {
            var alertKey = $"invalid:{result.Platform}";
            if (result.Status == ExtensionForceInstallStatus.Invalid)
            {
                if (!_activeExtensionAlerts.Add(alertKey)) continue; // already alerted, still broken - don't repeat every cycle

                logger.LogWarning(
                    "{Platform} extension force-install is misconfigured (missing/malformed/placeholder id or update URL) - " +
                    "BlockUnapprovedExtensions was NOT applied for this browser as a result.", result.Platform);
                await auditLogger.WriteAsync(new AuditEvent
                {
                    ActionKey = ActionKeys.PolicyApply,
                    EventType = "browser-extension-policy",
                    Action = $"validate-{result.Platform.ToString().ToLowerInvariant()}-extension",
                    Result = "invalid",
                    Details = $"{result.Platform} extension id/update URL is missing, malformed, or an unfilled placeholder. " +
                               "No force-install or blanket extension block was applied for this browser."
                }, cancellationToken);
            }
            else
            {
                _activeExtensionAlerts.Remove(alertKey);
            }
        }
    }

    // Best-effort: confirms the extension is actually present in the active console user's browser
    // profile, not just that the registry policy was written - see ExtensionHealthChecker for exactly
    // what this does and does not claim to verify. Wrapped in its own try/catch that only logs, never
    // rethrows - a health-check problem (a locked file, a WMI hiccup resolving the active user, ...)
    // must never be mistaken for, or cause, a policy-apply failure.
    private async Task RunExtensionHealthChecksAsync(List<ExtensionPolicyResult> results, CancellationToken cancellationToken)
    {
        try
        {
            var userSid = interactiveUserContextProvider.GetActiveConsoleUser().UserSid;
            if (string.IsNullOrWhiteSpace(userSid)) return;

            foreach (var result in results)
            {
                if (result.Status != ExtensionForceInstallStatus.Valid) continue;

                var present = result.Platform switch
                {
                    ExtensionPlatform.Chrome => extensionHealthChecker.IsChromeExtensionPresent(userSid, result.ExtensionId),
                    ExtensionPlatform.Edge => extensionHealthChecker.IsEdgeExtensionPresent(userSid, result.ExtensionId),
                    ExtensionPlatform.Firefox => extensionHealthChecker.IsFirefoxExtensionPresent(userSid, result.ExtensionId),
                    _ => true
                };

                var alertKey = $"missing:{result.Platform}";
                if (!present)
                {
                    if (!_activeExtensionAlerts.Add(alertKey)) continue;

                    logger.LogWarning(
                        "{Platform} force-installed extension {ExtensionId} was not found in the active user's browser profile - " +
                        "policy is configured but protection may not actually be running.", result.Platform, result.ExtensionId);
                    await auditLogger.WriteAsync(new AuditEvent
                    {
                        ActionKey = ActionKeys.PolicyApply,
                        EventType = "browser-extension-health",
                        Action = $"healthcheck-{result.Platform.ToString().ToLowerInvariant()}-extension",
                        Result = "missing",
                        Details = $"{result.Platform} extension {result.ExtensionId} is configured for force-install but was not found " +
                                   "in the active user's browser profile (presence check only, not enabled/disabled state - verify manually)."
                    }, cancellationToken);
                }
                else if (_activeExtensionAlerts.Remove(alertKey))
                {
                    await auditLogger.WriteAsync(new AuditEvent
                    {
                        ActionKey = ActionKeys.PolicyApply,
                        EventType = "browser-extension-health",
                        Action = $"healthcheck-{result.Platform.ToString().ToLowerInvariant()}-extension",
                        Result = "present",
                        Details = $"{result.Platform} extension {result.ExtensionId} is present again in the active user's browser profile."
                    }, cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Extension health check failed for this cycle; will retry next cycle.");
        }
    }

    private static void SetOrDeleteDword(RegistryKey key, string name, bool enabled, int enabledValue)
    {
        if (enabled) key.SetValue(name, enabledValue, RegistryValueKind.DWord);
        else key.DeleteValue(name, false);
    }
}
