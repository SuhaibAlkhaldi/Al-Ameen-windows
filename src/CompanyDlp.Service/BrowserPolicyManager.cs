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
            ApplyWindowsScreenCapturePolicy(policy.Screen);
            await auditLogger.WriteAsync(new AuditEvent
            {
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

        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var decision = permissionEvaluator.Evaluate(
            policy,
            ActionKeys.BrowserDownload,
            context,
            identityProvider.Get(),
            DateTimeOffset.UtcNow);

        return !decision.IsAllowed;
    }

    private static void ApplyWindowsScreenCapturePolicy(ScreenPolicy policy)
    {
        const string gameDvrPath = @"SOFTWARE\Policies\Microsoft\Windows\GameDVR";
        using var key = Registry.LocalMachine.CreateSubKey(gameDvrPath, true)
            ?? throw new InvalidOperationException("Could not open the Windows GameDVR policy registry key.");

        SetOrDeleteDword(key, "AllowGameDVR", policy.Enabled && policy.DisableWindowsGameCapture, 0);
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
