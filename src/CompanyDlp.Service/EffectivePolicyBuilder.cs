using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class EffectivePolicyBuilder(
    PermissionEvaluator evaluator,
    AgentIdentityProvider identityProvider)
{
    public DlpPolicy Build(DlpPolicy source, ClientContext context, DateTimeOffset nowUtc)
    {
        var json = JsonSerializer.Serialize(source, JsonDefaults.Options);
        var effective = JsonSerializer.Deserialize<DlpPolicy>(json, JsonDefaults.Options)
            ?? throw new InvalidOperationException("Could not clone the current DLP policy.");
        var identity = identityProvider.Get();

        if (evaluator.Evaluate(source, ActionKeys.ScreenCapture, context, identity, nowUtc).IsAllowed)
        {
            effective.Screen.BlockPrintScreenHotkey = false;
            effective.Screen.BlockWindowsSnippingShortcut = false;
            effective.Browser.DisableBrowserScreenshots = false;
            effective.Screen.MonitorKnownScreenshotToolProcesses = false;
        }

        if (evaluator.Evaluate(source, ActionKeys.ScreenRecording, context, identity, nowUtc).IsAllowed)
        {
            effective.Screen.BlockWindowsGameBarShortcuts = false;
            effective.Screen.DisableWindowsGameCapture = false;
            effective.Screen.MonitorKnownRecorderProcesses = false;
        }

        if (evaluator.Evaluate(source, ActionKeys.ClipboardCopySensitive, context, identity, nowUtc).IsAllowed)
        {
            effective.Clipboard.BlockSensitiveText = false;
            effective.Browser.BlockSensitiveCopy = false;
        }

        if (evaluator.Evaluate(source, ActionKeys.BrowserDownload, context, identity, nowUtc).IsAllowed)
            effective.Browser.BlockDownloads = false;

        if (evaluator.Evaluate(source, ActionKeys.BrowserUpload, context, identity, nowUtc).IsAllowed)
            effective.Browser.BlockFileUpload = false;

        if (evaluator.Evaluate(source, ActionKeys.BrowserDragDrop, context, identity, nowUtc).IsAllowed)
            effective.Browser.BlockDragAndDrop = false;

        if (evaluator.Evaluate(source, ActionKeys.BrowserFilePaste, context, identity, nowUtc).IsAllowed)
            effective.Browser.BlockFilePaste = false;

        if (evaluator.Evaluate(source, ActionKeys.BrowserImagePaste, context, identity, nowUtc).IsAllowed)
            effective.Browser.BlockImagePaste = false;

        return effective;
    }
}
