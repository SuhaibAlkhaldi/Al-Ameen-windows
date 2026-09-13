namespace CompanyDlp.Contracts;

public static class ActionKeys
{
    public const string AgentSession = "agent.session";
    public const string BrowserDownload = "browser.download";
    public const string ScreenCapture = "screen.capture";
    public const string ScreenRecording = "screen.recording";
    public const string ClipboardCopySensitive = "clipboard.copy-sensitive";
    public const string BrowserUpload = "browser.upload";
    public const string BrowserDragDrop = "browser.drag-drop";
    public const string BrowserFilePaste = "browser.file-paste";
    public const string BrowserImagePaste = "browser.image-paste";
    public const string UsbDeviceConnect = "usb.device-connect";
    public const string UsbStorage = "usb.storage";
    public const string UsbMobileDevice = "usb.mobile-device";
    public const string SoftwareInstall = "software.install";
    public const string SoftwareExecuteUnapproved = "software.execute-unapproved";
    public const string FileEncrypt = "file.encrypt";
    public const string FileDecrypt = "file.decrypt";
    public const string FilePrint = "file.print";
    public const string WatermarkDisable = "watermark.disable";

    // Distinct from WatermarkDisable above (which only ever governs the live on-screen overlay -
    // see MainWindow.xaml.cs's RefreshWatermarkGrantAsync/WatermarkManager). This one governs the
    // watermark stamped into a file's own content (ContentWatermarker) - a separate concept with
    // its own request/approval flow, since there is no "blocked attempt" moment to react to the way
    // print has; the employee self-initiates this from the Desktop app's Watermark section. Covers
    // BOTH of ContentWatermarker's layers: the tiled/repeating background AND the small corner info
    // block (Classification/Device) - a grant for a tier hides both, a revoke restores both.
    public const string FileWatermarkDisable = "file.watermark-disable";

    // FileOpenAccess: gates opening a file that was NOT created locally by the user themselves - see
    // FileProvenanceStore for how "received from outside" is detected (browser download, USB copy;
    // anything unmatched defaults to locally-created, i.e. open freely - there is no reliable way to
    // positively prove local authorship in user-mode). Distinct from FileDecrypt (the employee-facing
    // manual encrypt/decrypt self-service tool) even though both ultimately call into
    // FileProtectionEngine - this action represents a mandatory gate applied automatically by
    // FileInventoryScanner, not an opt-in convenience the employee triggers themselves. Applies to
    // EVERY classification tier, including Public (see ActionsRequiringGrantEvenForPublic below) -
    // a received file needs an explicit grant regardless of how sensitive its content is. An admin's
    // blanket "can open anything received, any tier" grant is just an ordinary grant with no
    // ClassificationTier/FileHash set (see PermissionEvaluator.MatchesFileScope) - no wildcard tier
    // value needed, same convention every other action-level grant already uses.
    public const string FileOpenAccess = "file.open-access";

    // CliExecute: presence/allow-deny channel - can this user launch cmd.exe/powershell.exe/
    // powershell_ise.exe/pwsh.exe/wt.exe at all. Enforced entirely by a per-user Explorer
    // DisallowRun/RestrictRun policy (CliExecutionPolicyManager) - AppLocker is deliberately not used
    // for any of these five; see that class's comment for the live-confirmed-twice reason (enabling
    // AppLocker's Exe rule collection at all freezes Windows Shell on affected devices, independent of
    // rule content). Explorer DisallowRun blocks do not generate a queryable Windows event of their
    // own, so there is currently no audit trail for an individual blocked launch attempt.
    public const string CliExecute = "cli.execute";

    // CliSensitiveCommand: content-classification channel, independent of CliExecute - when CLI
    // execution is allowed, the actual command text is classified for exfiltration/attack patterns
    // (CliSensitiveCommandMonitor). Every event reported under this key already represents a
    // detected match (nothing to gate an Allow/Block decision on), mirroring how
    // ClipboardCopySensitive audit events are only ever written when content classifies as sensitive.
    public const string CliSensitiveCommand = "cli.sensitive-command";

    // Not a gateable permission action (no DefaultPermissions entry, not in All below) - just the
    // audit-event action key PolicySyncWorker tags its own "applied a new policy" events with.
    public const string PolicyApply = "policy.apply";

    // IReadOnlySet<string> isn't available on netstandard2.0 (this project multi-targets net8.0 and
    // netstandard2.0 so CompanyDlp.ShellExtension, a .NET Framework 4.8 project, can reference it
    // directly) - HashSet<string> already exposes Contains/enumeration to every existing caller here.
    public static HashSet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AgentSession,
        BrowserDownload,
        ScreenCapture,
        ScreenRecording,
        ClipboardCopySensitive,
        BrowserUpload,
        BrowserDragDrop,
        BrowserFilePaste,
        BrowserImagePaste,
        UsbDeviceConnect,
        UsbStorage,
        UsbMobileDevice,
        SoftwareInstall,
        SoftwareExecuteUnapproved,
        FileEncrypt,
        FileDecrypt,
        FilePrint,
        WatermarkDisable,
        FileWatermarkDisable,
        FileOpenAccess,
        CliExecute,
        CliSensitiveCommand
    };
}
