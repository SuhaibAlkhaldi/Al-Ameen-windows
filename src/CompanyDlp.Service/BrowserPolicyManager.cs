using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Service;

public sealed class BrowserPolicyManager(
    PolicyStore policyStore,
    PermissionEvaluator permissionEvaluator,
    AgentIdentityProvider identityProvider,
    InteractiveUserContextProvider interactiveUserContextProvider,
    AuditLogger auditLogger,
    ILogger<BrowserPolicyManager> logger)
{
    private const string EdgePath = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string ChromePath = @"SOFTWARE\Policies\Google\Chrome";

    public async Task ApplyMachinePoliciesAsync(CancellationToken cancellationToken = default)
    {
        var policy = policyStore.Get();
        if (!OperatingSystem.IsWindows()) return;
        if (!policy.Enabled || (!policy.Browser.Enabled && !policy.Screen.Enabled)) return;
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase) || !policy.Runtime.PersistentProtection)
        {
            throw new InvalidOperationException("Machine browser policies are only applied in Production persistent mode.");
        }

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
                ApplyEdge(policy.Browser, blockDownloads);
                ApplyChrome(policy.Browser, blockDownloads);
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
                Details = "Browser and Windows screen-capture machine policies were applied."
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

    private static void ApplyEdge(BrowserPolicy policy, bool blockDownloads)
    {
        using var key = Registry.LocalMachine.CreateSubKey(EdgePath, true)
            ?? throw new InvalidOperationException("Could not open the Edge policy registry key.");

        SetOrDeleteDword(key, "InPrivateModeAvailability", policy.DisableIncognito, 1);
        SetOrDeleteDword(key, "BrowserGuestModeEnabled", policy.DisableGuestMode, 0);
        SetOrDeleteDword(key, "DisableScreenshots", policy.DisableBrowserScreenshots, 1);
        SetOrDeleteDword(key, "WebCaptureEnabled", policy.DisableBrowserScreenshots, 0);
        SetOrDeleteDword(key, "DownloadRestrictions", blockDownloads, 3);

        ApplyExtensionPolicy(Registry.LocalMachine, EdgePath, policy.EdgeExtensionId, policy.EdgeExtensionUpdateUrl, policy.BlockUnapprovedExtensions);
    }

    private static void ApplyChrome(BrowserPolicy policy, bool blockDownloads)
    {
        using var key = Registry.LocalMachine.CreateSubKey(ChromePath, true)
            ?? throw new InvalidOperationException("Could not open the Chrome policy registry key.");

        SetOrDeleteDword(key, "IncognitoModeAvailability", policy.DisableIncognito, 1);
        SetOrDeleteDword(key, "BrowserGuestModeEnabled", policy.DisableGuestMode, 0);
        SetOrDeleteDword(key, "DownloadRestrictions", blockDownloads, 3);

        ApplyExtensionPolicy(Registry.LocalMachine, ChromePath, policy.ChromeExtensionId, policy.ChromeExtensionUpdateUrl, policy.BlockUnapprovedExtensions);
    }

    private static void ApplyExtensionPolicy(RegistryKey hive, string browserPath, string extensionId, string updateUrl, bool blockOthers)
    {
        if (!string.IsNullOrWhiteSpace(extensionId) && !string.IsNullOrWhiteSpace(updateUrl))
        {
            using var forceList = hive.CreateSubKey($@"{browserPath}\ExtensionInstallForcelist", true);
            forceList?.SetValue("9999", $"{extensionId};{updateUrl}", RegistryValueKind.String);
        }

        if (blockOthers)
        {
            using var blockList = hive.CreateSubKey($@"{browserPath}\ExtensionInstallBlocklist", true);
            blockList?.SetValue("9999", "*", RegistryValueKind.String);
        }
    }

    private static void SetOrDeleteDword(RegistryKey key, string name, bool enabled, int enabledValue)
    {
        if (enabled) key.SetValue(name, enabledValue, RegistryValueKind.DWord);
        else key.DeleteValue(name, false);
    }
}
