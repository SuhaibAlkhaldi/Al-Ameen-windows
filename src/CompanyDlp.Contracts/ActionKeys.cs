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
    public const string WatermarkDisable = "watermark.disable";

    // CliExecute: presence/allow-deny channel - can this user launch cmd.exe/powershell.exe/pwsh.exe
    // at all. Enforced by AppLocker Deny rules (CliExecutionPolicyManager), audited from the
    // AppLocker event log (CliExecutionAuditMonitor).
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
        WatermarkDisable,
        CliExecute,
        CliSensitiveCommand
    };
}
