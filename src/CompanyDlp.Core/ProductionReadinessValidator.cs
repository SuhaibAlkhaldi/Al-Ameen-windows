using System.Security.Cryptography;
using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public static class ProductionReadinessValidator
{
    public static IReadOnlyList<string> Validate(DlpPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!policy.Runtime.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase))
            return [];

        var failures = new List<string>();
        Require(policy.Enabled, "Policy must be enabled.", failures);
        Require(policy.Runtime.PersistentProtection, "Production must enable persistent protection.", failures);
        Require(policy.Runtime.KeepSessionAgentRunning, "Production must keep the per-user session agent supervised.", failures);

        ValidateBackend(policy.Backend, failures);
        ValidateScreen(policy.Screen, failures);
        ValidateClipboard(policy.Clipboard, failures);
        ValidateBrowser(policy.Browser, failures);
        ValidateUsb(policy.Usb, failures);
        ValidateSoftware(policy.Software, failures);
        ValidateWatermark(policy.Watermark, failures);
        ValidateFileProtection(policy.FileProtection, policy.FileClassification, failures);
        ValidatePermissions(policy.Permissions, failures);

        return failures;
    }

    private static void ValidateBackend(BackendPolicy backend, List<string> failures)
    {
        Require(backend.Enabled, "Production backend synchronization must be enabled.", failures);
        Require(backend.TenantId != Guid.Empty, "Production backend tenantId must be configured.", failures);
        Require(backend.Mode.Equals("Production", StringComparison.OrdinalIgnoreCase), "Backend mode must be Production.", failures);
        Require(backend.AuthenticationMode.Equals(BackendAuthenticationModes.DeviceBearerToken, StringComparison.OrdinalIgnoreCase),
            "Production backend authentication must use DeviceBearerToken.", failures);
        Require(!backend.AllowUnsignedDevelopmentPolicy, "Production must not allow unsigned development policies.", failures);
        Require(!string.IsNullOrWhiteSpace(backend.CredentialName), "Production credentialName must be configured.", failures);

        if (!Uri.TryCreate(backend.BaseUrl, UriKind.Absolute, out var uri))
        {
            failures.Add("Production backend baseUrl must be an absolute HTTPS URL.");
        }
        else
        {
            Require(uri.Scheme == Uri.UriSchemeHttps, "Production backend baseUrl must use HTTPS.", failures);
            Require(!uri.IsLoopback, "Production backend baseUrl must not point to localhost.", failures);
        }

        if (string.IsNullOrWhiteSpace(backend.PolicySigningPublicKeyPem)
            || backend.PolicySigningPublicKeyPem.Contains("REPLACE_", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Production policy signing public key PEM must be configured.");
        }
        else
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(backend.PolicySigningPublicKeyPem);
            }
            catch
            {
                failures.Add("Production policy signing public key PEM must be a valid ECDSA public key.");
            }
        }
    }

    private static void ValidateScreen(ScreenPolicy screen, List<string> failures)
    {
        Require(screen.Enabled, "Screen protection must be enabled.", failures);
        Require(screen.BlockPrintScreenHotkey, "Print Screen blocking must be enabled.", failures);
        Require(screen.BlockWindowsSnippingShortcut, "Windows snipping shortcut blocking must be enabled.", failures);
        Require(screen.BlockWindowsGameBarShortcuts, "Windows Game Bar shortcut blocking must be enabled.", failures);
        Require(screen.DisableWindowsGameCapture, "Windows Game Capture policy must be disabled.", failures);
        Require(screen.MonitorKnownRecorderProcesses, "Known screen recorder process monitoring must be enabled.", failures);
        Require(screen.MonitorKnownScreenshotToolProcesses, "Known screenshot tool process monitoring must be enabled.", failures);
        Require(screen.RecorderEnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase)
                || screen.RecorderEnforcementMode.Equals("WindowsAppControl", StringComparison.OrdinalIgnoreCase),
            "Screen recorder enforcement must be Block or WindowsAppControl.", failures);
    }

    private static void ValidateClipboard(ClipboardPolicy clipboard, List<string> failures)
    {
        Require(clipboard.Enabled, "Clipboard protection must be enabled.", failures);
        Require(clipboard.BlockSensitiveText, "Sensitive clipboard copy blocking must be enabled.", failures);
        Require(clipboard.ClearClipboardOnBlock, "Clipboard must be cleared when sensitive copy is blocked.", failures);
    }

    private static void ValidateBrowser(BrowserPolicy browser, List<string> failures)
    {
        Require(browser.Enabled, "Browser protection must be enabled.", failures);
        Require(browser.BlockFileUpload, "Browser file upload blocking must be enabled.", failures);
        Require(browser.BlockDragAndDrop, "Browser drag and drop blocking must be enabled.", failures);
        Require(browser.BlockFilePaste, "Browser file paste blocking must be enabled.", failures);
        Require(browser.BlockImagePaste, "Browser image paste blocking must be enabled.", failures);
        Require(browser.BlockSensitiveCopy, "Browser sensitive copy blocking must be enabled.", failures);
        Require(browser.DisableIncognito, "Browser incognito mode must be disabled.", failures);
        Require(browser.DisableGuestMode, "Browser guest mode must be disabled.", failures);
        Require(browser.DisableBrowserScreenshots, "Browser screenshot controls must be enabled.", failures);
    }

    private static void ValidateUsb(UsbPolicy usb, List<string> failures)
    {
        Require(usb.Enabled, "USB protection must be enabled.", failures);
        Require(usb.EnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase)
                || usb.EnforcementMode.Equals("WindowsDeviceControl", StringComparison.OrdinalIgnoreCase),
            "USB enforcement must be Block or WindowsDeviceControl.", failures);
        Require(usb.AllowAnyKeyboardOrMouse, "USB keyboard and mouse devices must remain allowed.", failures);
        Require(!usb.TrustDevicesPresentAtFirstRun, "Production must not trust devices present at first run automatically.", failures);
        Require(usb.DenyCompositeDevicesWithForbiddenFunctions, "Composite USB devices with forbidden functions must be denied.", failures);
    }

    private static void ValidateSoftware(SoftwarePolicy software, List<string> failures)
    {
        Require(software.Enabled, "Software installation protection must be enabled.", failures);
        Require(software.EnforcementMode.Equals("Block", StringComparison.OrdinalIgnoreCase)
                || software.EnforcementMode.Equals("WindowsAppControl", StringComparison.OrdinalIgnoreCase),
            "Software enforcement must be Block or WindowsAppControl.", failures);
        Require(software.BlockMsi, "MSI installer blocking must be enabled.", failures);
        Require(software.BlockMsixAppx, "MSIX/AppX installer blocking must be enabled.", failures);
        Require(software.BlockKnownInstallers, "Known installer blocking must be enabled.", failures);
    }

    private static void ValidateWatermark(WatermarkPolicy watermark, List<string> failures)
    {
        Require(watermark.Enabled, "Watermark must be enabled.", failures);
        Require(watermark.IncludeMachineName, "Watermark must include the machine name.", failures);
        Require(watermark.IncludeTime, "Watermark must include date/time.", failures);
    }

    private static void ValidateFileProtection(
        FileProtectionPolicy fileProtection,
        FileClassificationPolicy fileClassification,
        List<string> failures)
    {
        Require(fileProtection.Enabled, "File encryption/decryption protection must be enabled.", failures);
        Require(fileProtection.DeletePlaintextAfterVerifiedEncryption, "Encryption must delete plaintext only after verified encryption.", failures);
        Require(fileClassification.Enabled, "File classification policy must be enabled.", failures);
        Require(fileClassification.FailClosed, "File classification must fail closed.", failures);
    }

    private static void ValidatePermissions(PermissionPolicy permissions, List<string> failures)
    {
        RequireDefaultDeny(permissions, ActionKeys.ScreenCapture, failures);
        RequireDefaultDeny(permissions, ActionKeys.ScreenRecording, failures);
        RequireDefaultDeny(permissions, ActionKeys.ClipboardCopySensitive, failures);
        RequireDefaultDeny(permissions, ActionKeys.BrowserUpload, failures);
        RequireDefaultDeny(permissions, ActionKeys.BrowserDragDrop, failures);
        RequireDefaultDeny(permissions, ActionKeys.BrowserFilePaste, failures);
        RequireDefaultDeny(permissions, ActionKeys.BrowserImagePaste, failures);
        RequireDefaultDeny(permissions, ActionKeys.UsbDeviceConnect, failures);
        RequireDefaultDeny(permissions, ActionKeys.UsbStorage, failures);
        RequireDefaultDeny(permissions, ActionKeys.UsbMobileDevice, failures);
        RequireDefaultDeny(permissions, ActionKeys.SoftwareInstall, failures);
        RequireDefaultDeny(permissions, ActionKeys.SoftwareExecuteUnapproved, failures);
    }

    private static void RequireDefaultDeny(PermissionPolicy permissions, string actionKey, List<string> failures)
    {
        if (!permissions.DefaultPermissions.TryGetValue(actionKey, out var allowed) || allowed)
            failures.Add($"Default permission for {actionKey} must be deny.");
    }

    private static void Require(bool condition, string message, List<string> failures)
    {
        if (!condition) failures.Add(message);
    }
}
