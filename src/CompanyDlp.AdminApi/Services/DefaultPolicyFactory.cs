using CompanyDlp.AdminApi.Configuration;
using CompanyDlp.Contracts;

namespace CompanyDlp.AdminApi.Services;

public static class DefaultPolicyFactory
{
    public static DlpPolicy Create(Guid tenantId, PolicyDeliveryOptions delivery) => new()
    {
        PolicyVersion = "central-1",
        Enabled = true,
        Runtime = new RuntimePolicy
        {
            Mode = delivery.Mode,
            PersistentProtection = string.Equals(delivery.Mode, "Production", StringComparison.OrdinalIgnoreCase),
            PolicyReapplySeconds = 15,
            KeepSessionAgentRunning = true,
            SessionAgentPollSeconds = 5
        },
        Screen = new ScreenPolicy
        {
            RecorderEnforcementMode = "Block"
        },
        Usb = new UsbPolicy
        {
            EnforcementMode = "Block"
        },
        Software = new SoftwarePolicy
        {
            EnforcementMode = "Block"
        },
        FileProtection = new FileProtectionPolicy
        {
            KeyProvider = "BackendKms",
            DeletePlaintextAfterVerifiedEncryption = true,
            KeepEncryptedFileAfterDecryption = true
        },
        Backend = new BackendPolicy
        {
            Enabled = true,
            TenantId = tenantId,
            Mode = delivery.Mode,
            BaseUrl = delivery.PublicBaseUrl,
            RequestTimeoutSeconds = 15,
            AuditBatchSize = 100,
            AuditSyncSeconds = 5,
            PolicySyncSeconds = 30,
            HeartbeatSeconds = 15,
            AllowUnsignedDevelopmentPolicy = delivery.AllowUnsignedDevelopmentSnapshots,
            AuthenticationMode = BackendAuthenticationModes.DeviceBearerToken,
            CredentialName = "agent-access-token"
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
        },
        SensitiveRules =
        [
            new SensitiveRule
            {
                Id = "keyword-confidential",
                Name = "Confidential keyword",
                Type = SensitiveRuleTypes.Keyword,
                Value = "confidential",
                DetectFragments = false
            },
            new SensitiveRule
            {
                Id = "any-email-address",
                Name = "Block every email address",
                Type = SensitiveRuleTypes.AnyEmail,
                Enabled = true,
                DetectFragments = true
            }
        ]
    };
}
