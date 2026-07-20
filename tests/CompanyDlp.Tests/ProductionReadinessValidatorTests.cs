using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class ProductionReadinessValidatorTests
{
    [Fact]
    public void DevelopmentPolicy_DoesNotRequireProductionHardening()
    {
        var policy = new DlpPolicy
        {
            Runtime = new RuntimePolicy { Mode = "Development" },
            Backend = new BackendPolicy
            {
                Mode = "Mock",
                BaseUrl = "http://127.0.0.1:5055",
                AuthenticationMode = BackendAuthenticationModes.DevelopmentNone,
                AllowUnsignedDevelopmentPolicy = true
            }
        };

        var failures = ProductionReadinessValidator.Validate(policy);

        Assert.Empty(failures);
    }

    [Fact]
    public void ProductionPolicy_RejectsInsecureBackendSettings()
    {
        var policy = CreateStrictProductionPolicy();
        policy.Backend.BaseUrl = "http://127.0.0.1:5055";
        policy.Backend.AuthenticationMode = BackendAuthenticationModes.DevelopmentNone;
        policy.Backend.AllowUnsignedDevelopmentPolicy = true;
        policy.Backend.PolicySigningPublicKeyPem = "";

        var failures = ProductionReadinessValidator.Validate(policy);

        Assert.Contains(failures, item => item.Contains("HTTPS", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failures, item => item.Contains("DeviceBearerToken", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failures, item => item.Contains("unsigned development policies", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(failures, item => item.Contains("policy signing public key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StrictProductionPolicy_PassesWindowsReadinessChecks()
    {
        var policy = CreateStrictProductionPolicy();

        var failures = ProductionReadinessValidator.Validate(policy);

        Assert.Empty(failures);
    }

    private static DlpPolicy CreateStrictProductionPolicy()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new DlpPolicy
        {
            PolicyVersion = "test-production",
            Enabled = true,
            Runtime = new RuntimePolicy
            {
                Mode = "Production",
                PersistentProtection = true,
                KeepSessionAgentRunning = true
            },
            Clipboard = new ClipboardPolicy
            {
                Enabled = true,
                BlockSensitiveText = true,
                ClearClipboardOnBlock = true
            },
            Browser = new BrowserPolicy
            {
                Enabled = true,
                DisableIncognito = true,
                DisableGuestMode = true,
                DisableBrowserScreenshots = true,
                BlockFileUpload = true,
                BlockDragAndDrop = true,
                BlockFilePaste = true,
                BlockImagePaste = true,
                BlockSensitiveCopy = true
            },
            Usb = new UsbPolicy
            {
                Enabled = true,
                EnforcementMode = "Block",
                AllowAnyKeyboardOrMouse = true,
                TrustDevicesPresentAtFirstRun = false,
                DenyCompositeDevicesWithForbiddenFunctions = true
            },
            Screen = new ScreenPolicy
            {
                Enabled = true,
                BlockPrintScreenHotkey = true,
                BlockWindowsSnippingShortcut = true,
                BlockWindowsGameBarShortcuts = true,
                DisableWindowsGameCapture = true,
                MonitorKnownRecorderProcesses = true,
                RecorderEnforcementMode = "Block"
            },
            Watermark = new WatermarkPolicy
            {
                Enabled = true,
                IncludeMachineName = true,
                IncludeTime = true
            },
            Software = new SoftwarePolicy
            {
                Enabled = true,
                EnforcementMode = "WindowsAppControl",
                BlockMsi = true,
                BlockMsixAppx = true,
                BlockKnownInstallers = true
            },
            FileProtection = new FileProtectionPolicy
            {
                Enabled = true,
                DeletePlaintextAfterVerifiedEncryption = true
            },
            FileClassification = new FileClassificationPolicy
            {
                Enabled = true,
                FailClosed = true
            },
            Backend = new BackendPolicy
            {
                Enabled = true,
                TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Mode = "Production",
                BaseUrl = "https://dlp-api.company.example",
                AllowUnsignedDevelopmentPolicy = false,
                AuthenticationMode = BackendAuthenticationModes.DeviceBearerToken,
                CredentialName = "agent-access-token",
                PolicySigningPublicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem()
            },
            Permissions = new PermissionPolicy
            {
                DefaultPermissions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [ActionKeys.ScreenCapture] = false,
                    [ActionKeys.ScreenRecording] = false,
                    [ActionKeys.ClipboardCopySensitive] = false,
                    [ActionKeys.BrowserUpload] = false,
                    [ActionKeys.BrowserDragDrop] = false,
                    [ActionKeys.BrowserFilePaste] = false,
                    [ActionKeys.BrowserImagePaste] = false,
                    [ActionKeys.UsbDeviceConnect] = false,
                    [ActionKeys.UsbStorage] = false,
                    [ActionKeys.UsbMobileDevice] = false,
                    [ActionKeys.SoftwareInstall] = false,
                    [ActionKeys.SoftwareExecuteUnapproved] = false,
                    [ActionKeys.FileEncrypt] = true,
                    [ActionKeys.FileDecrypt] = true
                }
            }
        };
    }
}
