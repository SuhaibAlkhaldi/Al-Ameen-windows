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
    public CliPolicy Cli { get; set; } = new();
    public FileProtectionPolicy FileProtection { get; set; } = new();
    public PrintPolicy Print { get; set; } = new();
    public FileOpenProtectionPolicy FileOpenProtection { get; set; } = new();
    public DesktopAppProvenancePolicy DesktopAppProvenance { get; set; } = new();
    public FileClassificationPolicy FileClassification { get; set; } = new();
    public FileInventorySyncPolicy FileInventorySync { get; set; } = new();
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

    // FirefoxExtensionUpdateUrl must point at a signed .xpi (Firefox Release refuses to install an
    // unsigned one even via enterprise policy) - see scripts/pack-firefox-extension.ps1 for the
    // one-time manual Mozilla Add-on signing step this depends on.
    public string FirefoxExtensionId { get; set; } = "";
    public string FirefoxExtensionUpdateUrl { get; set; } = "";
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

// v1: this mode name is kept as "AppLocker" for backend/config compatibility even though AppLocker is
// no longer used anywhere in this feature - see CliExecutionPolicyManager's class comment for the full,
// live-confirmed-twice story of why. Simply enabling AppLocker's Exe rule collection at all (regardless
// of rule content or targeting) was proven to freeze Windows Shell's Start Menu/Search on affected
// devices, so this value now means "CLI blocking is turned on"; the actual mechanism is always a
// per-user Explorer DisallowRun/RestrictRun policy for all five restricted executables. "AuditOnly"
// pushes nothing; only "AppLocker" pushes/withdraws that policy. Do not reintroduce AppLocker here.
public static class CliEnforcementModes
{
    public const string AuditOnly = "AuditOnly";
    public const string AppLocker = "AppLocker";
}

public sealed class CliPolicy
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = CliEnforcementModes.AuditOnly;

    // Documents the full set of CLI executables this policy restricts; the actual enforcement mechanism
    // (a per-user Explorer DisallowRun/RestrictRun policy for all five) is hardcoded in
    // CliExecutionPolicyManager, not driven by this list. pwsh.exe (PowerShell 7+), powershell_ise.exe,
    // and wt.exe (Windows Terminal) are included even though they may not be installed on every device -
    // restricting a program that doesn't exist locally is simply inert, not an error.
    public List<string> RestrictedExecutableNames { get; set; } =
    [
        "cmd.exe", "powershell.exe", "powershell_ise.exe", "pwsh.exe", "wt.exe"
    ];

    // Independent of Enabled/EnforcementMode above by design - CliSensitiveCommand detection (see
    // ActionKeys.CliSensitiveCommand) stays on even when CLI execution itself is allowed, same as
    // the product's other "detect regardless of the gate decision" paths (integrity-hash mismatch).
    public bool SensitiveCommandDetectionEnabled { get; set; } = true;
}

public sealed class FileProtectionPolicy
{
    public bool Enabled { get; set; } = true;
    public string KeyProvider { get; set; } = "LocalMachineDpapi";
    public bool DeletePlaintextAfterVerifiedEncryption { get; set; } = true;
    public bool KeepEncryptedFileAfterDecryption { get; set; } = true;
    public long MaximumFileSizeBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

public sealed class PrintPolicy
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = "AuditOnly";
}

// Gates ActionKeys.FileOpenAccess - automatically encrypting/decrypting a classified file received
// from an external source (see FileProvenanceStore) based on whether an admin grant currently covers
// it, enforced by FileInventoryScanner. AuditOnly (the default) lets an admin observe how many/which
// files would be affected before actually locking anything - same rollout-safety pattern as
// UsbPolicy/PrintPolicy's own EnforcementMode.
public sealed class FileOpenProtectionPolicy
{
    public bool Enabled { get; set; } = true;
    public string EnforcementMode { get; set; } = "AuditOnly";

    // How long (minutes) a USB content snapshot is kept in UsbSnapshotCache after the drive was last
    // seen connected, so a file copied and the drive ejected before the next scan tick still matches.
    public int UsbSnapshotRetentionMinutes { get; set; } = 10;

    // Per-drive caps on the USB snapshot walk (UsbSnapshotCache) - bounds worst-case cost on a large
    // removable drive; a drive exceeding either cap is snapshotted only up to the cap (files beyond it
    // simply won't be matchable against, falling back to the default "self-created" assumption).
    public int UsbSnapshotMaxFiles { get; set; } = 5000;
    public long UsbSnapshotMaxTotalBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    // How long (seconds) FileInventoryScanner waits after first discovering a new file before
    // deciding its provenance - gives an in-flight BrowserDownload/USB signal time to land before the
    // "no matching channel -> self-created" default is applied. See FileProvenanceStore.
    public int NewFileProvenanceBufferSeconds { get; set; } = 3;
}

// Gates the file.open-access "desktop app" channel (FileProvenanceChannels.DesktopApp) - detecting a
// file that landed via some desktop application (Outlook, Teams, or any other, deliberately not
// app-specific) receiving it from the network, rather than a browser download or USB copy (the two
// channels this feature originally shipped with). Enforced by DesktopAppProvenanceMonitor.
//
// Off by default, unlike UsbPolicy/BrowserPolicy which have no such switch for their own channels -
// two reasons. First, this is the first feature in the agent with an external NuGet dependency
// (Microsoft.Diagnostics.Tracing.TraceEvent) and a brand-new correlation heuristic, so an admin opts
// in deliberately rather than it silently affecting file.open-access decisions the moment this version
// deploys. Second, and more fundamentally: unlike the other two channels, this one cannot attribute a
// newly-written file to the specific process that wrote it - Windows only exposes that as a real-time
// kernel-level file-close event (Microsoft-Windows-Kernel-File), which a Phase-0 spike against this
// exact agent (confirmed live 2026-09-09) found does not reliably fire through a normal, isolated ETW
// session; the only alternative Windows offers is the classic single-consumer-per-machine "NT Kernel
// Logger", which risks starving any other security/monitoring tool on the device that also needs it -
// unacceptable for a background DLP agent to gamble with. So detection here instead pairs a plain
// FileSystemWatcher on FileClassification.WatchedFolders (tells us WHEN a file appeared/stabilized,
// no process attribution) with the network side of the same original ETW plan, which the same spike
// confirmed DOES work reliably as a normal isolated session (Microsoft-Windows-Kernel-Network) - see
// NetworkActivityCache. A file is attributed to a process only when EXACTLY ONE non-excluded process
// had inbound network activity within CorrelationWindowSeconds of the file appearing
// (NetworkActivityCache.TryGetSoleRecentlyActiveProcessId); zero or multiple candidates is treated as
// unattributable and the file is left SelfCreated by default rather than guessed at - a missed
// detection here is far cheaper than wrongly auto-encrypting a user's own file.
public sealed class DesktopAppProvenancePolicy
{
    public bool Enabled { get; set; }
    public int CorrelationWindowSeconds { get; set; } = 20;

    // How many distinct process IDs NetworkActivityCache keeps timestamps for at once - bounds worst-case
    // memory on a busy machine; oldest entries are evicted first once this cap is hit.
    public int NetworkActivityCacheMaxTrackedProcesses { get; set; } = 2000;

    // Processes never attributed a file to, regardless of network activity. Browsers are excluded
    // because a browser download is already covered, exactly and more directly, by
    // FileProvenanceChannels.BrowserDownload - this channel must not double-report (or, worse, race)
    // the same file. CompanyDlp's own processes are excluded so this monitor can never attribute a file
    // to itself (e.g. the watermark escrow store's own temp-file writes touching a watched folder).
    public List<string> ExcludedProcessNames { get; set; } =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "iexplore",
        "CompanyDlp.Service", "CompanyDlp.Desktop", "CompanyDlp.NativeHost"
    ];
}

// File Inventory Report feature: syncs a current-state view of every file under
// FileClassification.WatchedFolders to the backend's FileInventoryRecords table, via three
// mechanisms (see FileInventorySyncWorker): an Initial Full Sync (once, on first run),
// Incremental Sync (FileInventoryChangeWatcher's FileSystemWatcher-detected changes, drained from
// FileInventoryOutbox), and a Periodic Reconciliation Scan (fallback full walk + diff, catches drift
// from e.g. the agent being offline when a change happened). On by default - unlike
// DesktopAppProvenance, this has no heuristic/guessing component and no new external dependency, it's
// pure "report what FileClassification already knows" plumbing.
public sealed class FileInventorySyncPolicy
{
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 200;
    public int IncrementalSyncSeconds { get; set; } = 15;
    public int ReconciliationScanIntervalMinutes { get; set; } = 60;
    public string BackendPath { get; set; } = "api/v1/agent/file-inventory/batch";
    public int TimeoutSeconds { get; set; } = 30;
}
