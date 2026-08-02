using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
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

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan DuplicateSuppressWindow = TimeSpan.FromSeconds(3);

    private HwndSource? _source;
    private DispatcherTimer? _pollTimer;

    private IntPtr _handle;
    private bool _clipboardListenerRegistered;
    private bool _ignoreNext;
    private int _processing;

    private string? _lastInspectedText;
    private string? _startupClipboardText;
    private DateTimeOffset _lastInspectedAtUtc = DateTimeOffset.MinValue;
    private bool _startupBaselineCaptured;
    private bool _clipboardChangedAfterStartup;

    public void Start()
    {
        InitializeClipboardListener();

        CaptureStartupClipboardBaseline();

        StartFallbackPolling();

        statusCallback("Monitoring clipboard text and fragment sequences");
    }

    private void InitializeClipboardListener()
    {
        _handle = new WindowInteropHelper(owner).Handle;

        if (_handle == IntPtr.Zero)
        {
            owner.SourceInitialized += OnOwnerSourceInitialized;
            statusCallback("Clipboard monitor waiting for window handle; fallback polling active.");
            return;
        }

        _source = HwndSource.FromHwnd(_handle);

        if (_source is null)
        {
            statusCallback("Clipboard window hook was not available; fallback polling active.");
            return;
        }

        _source.AddHook(WndProc);

        _clipboardListenerRegistered = AddClipboardFormatListener(_handle);

        if (!_clipboardListenerRegistered)
        {
            var error = Marshal.GetLastWin32Error();
            statusCallback($"Clipboard listener registration failed; fallback polling active. Win32Error={error}");
        }
    }

    private void OnOwnerSourceInitialized(object? sender, EventArgs e)
    {
        owner.SourceInitialized -= OnOwnerSourceInitialized;

        if (_clipboardListenerRegistered)
        {
            return;
        }

        InitializeClipboardListener();
        CaptureStartupClipboardBaseline();
    }

    private void CaptureStartupClipboardBaseline()
    {
        if (_startupBaselineCaptured)
        {
            return;
        }

        try
        {
            var existingText = ReadClipboardText();

            _startupClipboardText = existingText;
            _lastInspectedText = existingText;
            _lastInspectedAtUtc = DateTimeOffset.UtcNow;
            _startupBaselineCaptured = true;
            _clipboardChangedAfterStartup = false;
        }
        catch
        {
            _startupClipboardText = string.Empty;
            _lastInspectedText = string.Empty;
            _lastInspectedAtUtc = DateTimeOffset.UtcNow;
            _startupBaselineCaptured = true;
            _clipboardChangedAfterStartup = false;
        }
    }

    private void StartFallbackPolling()
    {
        _pollTimer?.Stop();

        _pollTimer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = PollInterval
        };

        _pollTimer.Tick += async (_, _) => await InspectClipboardAsync();
        _pollTimer.Start();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && !_ignoreNext)
        {
            _ = InspectClipboardAsync();
        }

        return IntPtr.Zero;
    }

    private async Task InspectClipboardAsync()
    {
        if (Interlocked.Exchange(ref _processing, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_startupBaselineCaptured)
            {
                CaptureStartupClipboardBaseline();
            }

            var text = await ReadClipboardTextAsync();

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            if (!_clipboardChangedAfterStartup)
            {
                if (string.Equals(text, _startupClipboardText, StringComparison.Ordinal))
                {
                    return;
                }

                _clipboardChangedAfterStartup = true;
            }

            var nowUtc = DateTimeOffset.UtcNow;

            if (string.Equals(text, _lastInspectedText, StringComparison.Ordinal) &&
                nowUtc - _lastInspectedAtUtc < DuplicateSuppressWindow)
            {
                return;
            }

            _lastInspectedText = text;
            _lastInspectedAtUtc = nowUtc;

            statusCallback($"Clipboard changed. Text length={text.Length}. Sending to DLP service...");

var response = await pipeClient.SendAsync(DlpMessageTypes.ClassifyText, new ClassificationRequest
{
    Text = text,
    Channel = "clipboard-copy",
    SessionKey = $"{Environment.UserDomainName}\\{Environment.UserName}",
    TrackFragments = true
});

if (response is null)
{
    statusCallback("Clipboard classification failed: no response from DLP service.");
    return;
}

if (response.Data is null)
{
    statusCallback("Clipboard classification failed: DLP service returned no data.");
    return;
}

ClassificationResult? result;

try
{
    result = response.Data.Value.Deserialize<ClassificationResult>(JsonDefaults.Options);
}
catch (Exception ex)
{
    statusCallback($"Clipboard classification failed: invalid response. {ex.Message}");
    return;
}

if (result is null)
{
    statusCallback("Clipboard classification failed: empty classification result.");
    return;
}

statusCallback(
    $"Clipboard classification result: IsSensitive={result.IsSensitive}, Matches={result.Matches.Count}, BlockSensitiveText={policy.BlockSensitiveText}");

if (!ClipboardBlockDecision.ShouldBlock(policy.BlockSensitiveText, result.IsSensitive, result.AllowedByGrant))
{
    var reason = !policy.BlockSensitiveText
        ? "Clipboard sensitive copy is allowed by effective policy; not blocking."
        : !result.IsSensitive
            ? "Clipboard text is not sensitive; not blocking."
            : "Clipboard sensitive copy is allowed by an approved permission grant; not blocking.";
    statusCallback(reason);
    return;
}

await BlockClipboardAsync(result);
        }
        finally
        {
            Interlocked.Exchange(ref _processing, 0);
        }
    }

    private async Task<string> ReadClipboardTextAsync()
    {
        return await owner.Dispatcher.InvokeAsync(ReadClipboardText);
    }

    private string ReadClipboardText()
    {
        try
        {
            if (WpfClipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
            {
                return WpfClipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
            }

            if (WpfClipboard.ContainsText())
            {
                return WpfClipboard.GetText();
            }
        }
        catch (ExternalException)
        {
            statusCallback("Clipboard is temporarily locked by another process.");
        }

        return string.Empty;
    }

    private async Task BlockClipboardAsync(ClassificationResult result)
    {
        _ignoreNext = true;

        try
        {
            await owner.Dispatcher.InvokeAsync(() =>
            {
                if (policy.ClearClipboardOnBlock)
                {
                    ClearClipboardWithRetry();
                }

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
            });
        }
        finally
        {
            await Task.Delay(150);
            _ignoreNext = false;
        }
    }

    private void ClearClipboardWithRetry()
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                WpfClipboard.Clear();

                _lastInspectedText = string.Empty;
                _lastInspectedAtUtc = DateTimeOffset.UtcNow;
                _startupClipboardText = string.Empty;
                _clipboardChangedAfterStartup = true;

                return;
            }
            catch (ExternalException)
            {
                if (attempt == 3)
                {
                    statusCallback("Sensitive content detected, but clipboard could not be cleared because it is locked.");
                    return;
                }

                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        owner.SourceInitialized -= OnOwnerSourceInitialized;

        if (_pollTimer is not null)
        {
            _pollTimer.Stop();
            _pollTimer = null;
        }

        if (_clipboardListenerRegistered && _handle != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_handle);
            _clipboardListenerRegistered = false;
        }

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