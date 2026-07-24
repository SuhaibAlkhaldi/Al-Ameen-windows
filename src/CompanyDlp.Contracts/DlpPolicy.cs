namespace CompanyDlp.Contracts;

public sealed class DlpPolicy
{
    public string PolicyVersion { get; set; } = "1.0";
    public bool Enabled { get; set; } = true;
    public RuntimePolicy Runtime { get; set; } = new();
    public ClipboardPolicy Clipboard { get; set; } = new();
    public BrowserPolicy Browser { get; set; } = new();
    public UsbPolicy Usb { get; set; } = new();
    public ScreenPolicy Screen { get; set; } = new();
    public WatermarkPolicy Watermark { get; set; } = new();
    public NotificationPolicy Notifications { get; set; } = new();
    public SoftwarePolicy Software { get; set; } = new();
    public FileProtectionPolicy FileProtection { get; set; } = new();
    public FileClassificationPolicy FileClassification { get; set; } = new();
    public BackendPolicy Backend { get; set; } = new();
    public PermissionPolicy Permissions { get; set; } = new();
    public List<SensitiveRule> SensitiveRules { get; set; } = [];
}

public sealed class RuntimePolicy
{
    public string Mode { get; set; } = "Development";
    public bool PersistentProtection { get; set; }
    public int PolicyReapplySeconds { get; set; } = 15;
    public string AuditDirectory { get; set; } = "";
    public bool KeepSessionAgentRunning { get; set; } = true;
    public int SessionAgentPollSeconds { get; set; } = 5;
}

public sealed class ClipboardPolicy
{
    public bool Enabled { get; set; } = true;
    public bool BlockSensitiveText { get; set; } = true;
    public bool ClearClipboardOnBlock { get; set; } = true;
    public int FragmentWindowSeconds { get; set; } = 300;
    public int MaxFragments { get; set; } = 12;
}

public sealed class BrowserPolicy
{
    public bool Enabled { get; set; } = true;
    public bool DisableIncognito { get; set; } = true;
    public bool DisableGuestMode { get; set; } = true;
    public bool DisableBrowserScreenshots { get; set; } = true;
    public bool BlockDownloads { get; set; } = true;
    public bool BlockFileUpload { get; set; } = true;
    public bool BlockDragAndDrop { get; set; } = true;
    public bool BlockFilePaste { get; set; } = true;
    public bool BlockImagePaste { get; set; } = true;
    public bool BlockSensitiveCopy { get; set; } = true;
    public bool BlockSensitiveInputAndSubmit { get; set; } = true;
    public bool ShowWatermark { get; set; } = true;
    public bool BlockUnapprovedExtensions { get; set; }
    public string ChromeExtensionId { get; set; } = "";
    public string ChromeExtensionUpdateUrl { get; set; } = "";
    public string EdgeExtensionId { get; set; } = "";
    public string EdgeExtensionUpdateUrl { get; set; } = "";
}

public sealed class UsbPolicy
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = "AuditOnly";
    public bool AllowAnyKeyboardOrMouse { get; set; } = true;
    public bool TrustDevicesPresentAtFirstRun { get; set; } = true;
    public int PollSeconds { get; set; } = 2;
    public bool LockWorkstationOnBlockedDevice { get; set; }
    public bool DenyCompositeDevicesWithForbiddenFunctions { get; set; } = true;
    public List<string> ApprovedHardwareIds { get; set; } = [];
    public List<string> ApprovedVidPid { get; set; } = [];
    public List<string> ApprovedSerialNumbers { get; set; } = [];
}

public sealed class ScreenPolicy
{
    public bool Enabled { get; set; } = true;
    public bool ProtectDlpWindowFromCapture { get; set; } = true;
    public bool BlockPrintScreenHotkey { get; set; } = true;
    public bool BlockWindowsSnippingShortcut { get; set; } = true;
    public bool BlockWindowsGameBarShortcuts { get; set; } = true;
    public bool DisableWindowsGameCapture { get; set; } = true;
    public bool MonitorKnownRecorderProcesses { get; set; } = true;
    public bool MonitorKnownScreenshotToolProcesses { get; set; } = true;
    public int RecorderPollMilliseconds { get; set; } = 250;
    public string RecorderEnforcementMode { get; set; } = "AuditOnly";

    // Genuine screen-recording software — gated by the ScreenRecording permission.
    public List<string> BlockedRecorderProcessNames { get; set; } =
    [
        "obs64", "obs32", "ShareX", "GameBar",
        "CamtasiaRecorder", "CamtasiaStudio", "Bandicam", "ScreenRecorder", "Loom"
    ];

    // Screenshot-only tools — these are the same capability the ScreenCapture permission is meant to
    // grant, so they must be gated by ScreenCapture, not lumped in with recorders under ScreenRecording.
    public List<string> BlockedScreenshotToolProcessNames { get; set; } =
    [
        "SnippingTool", "ScreenClippingHost"
    ];
}

public sealed class WatermarkPolicy
{
    public bool Enabled { get; set; } = true;
    public double Opacity { get; set; } = 0.22;
    public int FontSize { get; set; } = 18;
    public int HorizontalSpacing { get; set; } = 520;
    public int VerticalSpacing { get; set; } = 180;
    public string Prefix { get; set; } = "";
    public bool IncludeUsername { get; set; } = true;
    public bool IncludeMachineName { get; set; } = true;
    public bool IncludeTime { get; set; } = true;
    public bool IncludeSessionId { get; set; } = false;
}

public sealed class NotificationPolicy
{
    public bool Enabled { get; set; } = true;
    public int DurationSeconds { get; set; } = 6;
    public bool ShowRuleName { get; set; } = true;
    public bool ShowBrowserPageAlerts { get; set; } = false;
    public int DuplicateWindowSeconds { get; set; } = 3;
    public string Position { get; set; } = "TopRight";
}

public static class SensitiveRuleTypes
{
    public const string Keyword = "Keyword";
    public const string ExactValue = "ExactValue";
    public const string Regex = "Regex";
    public const string AnyEmail = "AnyEmail";
}

public sealed class SensitiveRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sensitive rule";
    public string Type { get; set; } = SensitiveRuleTypes.Keyword;
    public string Value { get; set; } = "";
    public string Pattern { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Normalize { get; set; } = true;
    public bool DetectFragments { get; set; } = true;
    public bool BlockIndividualFragments { get; set; }
    public int MinimumBlockedFragmentLength { get; set; } = 3;
}


public sealed class SoftwarePolicy
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = "AuditOnly";
    public bool BlockMsi { get; set; } = true;
    public bool BlockMsixAppx { get; set; } = true;
    public bool BlockKnownInstallers { get; set; } = true;
    public bool RequireTrustedPublisher { get; set; }
    public List<string> AllowedPublishers { get; set; } = [];
    public List<string> AllowedSha256 { get; set; } = [];
}

public sealed class FileProtectionPolicy
{
    public bool Enabled { get; set; } = true;
    public string KeyProvider { get; set; } = "LocalMachineDpapi";
    public bool DeletePlaintextAfterVerifiedEncryption { get; set; } = true;
    public bool KeepEncryptedFileAfterDecryption { get; set; } = true;
    public long MaximumFileSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}
