using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WpfClipboard = System.Windows.Clipboard;
using CompanyDlp.Contracts;
using CompanyDlp.Desktop.Services;

namespace CompanyDlp.Desktop.Protection;

public sealed class ClipboardProtectionManager(
    Window owner,
    PipeClient pipeClient,
    ClipboardPolicy policy,
    Action<string> statusCallback,
    Action<string, string> alertCallback) : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _ignoreNext;
    private int _processing;

    public void Start()
    {
        _handle = new WindowInteropHelper(owner).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);
        AddClipboardFormatListener(_handle);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && !_ignoreNext && Interlocked.Exchange(ref _processing, 1) == 0)
        {
            _ = InspectClipboardAsync();
        }
        return IntPtr.Zero;
    }

    private async Task InspectClipboardAsync()
    {
        try
        {
            string text = "";
            await owner.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (WpfClipboard.ContainsText()) text = WpfClipboard.GetText();
                }
                catch (ExternalException) { }
            });

            if (string.IsNullOrWhiteSpace(text)) return;
            var response = await pipeClient.SendAsync(DlpMessageTypes.ClassifyText, new ClassificationRequest
            {
                Text = text,
                Channel = "clipboard-copy",
                SessionKey = $"{Environment.UserDomainName}\\{Environment.UserName}",
                TrackFragments = true
            });
            var result = response.Data?.Deserialize<ClassificationResult>(JsonDefaults.Options);
            if (result?.IsSensitive != true || !policy.BlockSensitiveText) return;

            await owner.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _ignoreNext = true;
                    if (policy.ClearClipboardOnBlock) WpfClipboard.Clear();
                    var ruleName = result.Matches.FirstOrDefault()?.RuleName ?? "Sensitive content detected";
                    var status = result.FragmentAssemblyDetected
                        ? "Blocked: sensitive value was assembled from multiple copy operations."
                        : $"Blocked by rule: {ruleName}";
                    statusCallback(status);
                    alertCallback(
                        "Sensitive data copy blocked",
                        result.FragmentAssemblyDetected
                            ? "The copied text was blocked because it completes a sensitive value assembled from multiple copy operations."
                            : $"Copying this content is not allowed. Reason: {ruleName}.");
                }
                catch (ExternalException) { }
                finally
                {
                    _ignoreNext = false;
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) RemoveClipboardFormatListener(_handle);
        _source?.RemoveHook(WndProc);
        _source = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
