using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CompanyDlp.Contracts;
using CompanyDlp.Desktop.Services;

namespace CompanyDlp.Desktop.Protection;

public sealed class ScreenCaptureHotkeyBlocker : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyUp = 0x0105;
    private const int VkSnapshot = 0x2C;
    private const int VkS = 0x53;
    private const int VkR = 0x52;
    private const int VkG = 0x47;
    private const int VkMenu = 0x12;
    private const int VkLwin = 0x5B;
    private const int VkRwin = 0x5C;
    private const int VkShift = 0x10;

    private readonly ScreenPolicy _policy;
    private readonly PipeClient _pipeClient;
    private readonly Action<string> _statusCallback;
    private readonly Action<string, string> _alertCallback;
    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hook;

    public ScreenCaptureHotkeyBlocker(ScreenPolicy policy, PipeClient pipeClient, Action<string> statusCallback, Action<string, string> alertCallback)
    {
        _policy = policy;
        _pipeClient = pipeClient;
        _statusCallback = statusCallback;
        _alertCallback = alertCallback;
        _callback = HookCallback;
    }

    public void Start()
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(currentModule?.ModuleName), 0);
        if (_hook == IntPtr.Zero)
        {
            _statusCallback($"Screen-capture keyboard hook failed. Win32 error: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown || wParam == (IntPtr)WmKeyUp || wParam == (IntPtr)WmSysKeyUp))
        {
            var key = Marshal.ReadInt32(lParam);
            var blockPrintScreen = _policy.BlockPrintScreenHotkey && key == VkSnapshot;
            var winPressed = IsPressed(VkLwin) || IsPressed(VkRwin);
            var shiftPressed = IsPressed(VkShift);
            var altPressed = IsPressed(VkMenu);
            var blockSnipping = _policy.BlockWindowsSnippingShortcut && key == VkS && winPressed && shiftPressed;
            var blockGameBarRecording = _policy.BlockWindowsGameBarShortcuts && key == VkR && winPressed && altPressed;
            var blockGameBarOpen = _policy.BlockWindowsGameBarShortcuts && key == VkG && winPressed;

            if (blockPrintScreen || blockSnipping || blockGameBarRecording || blockGameBarOpen)
            {
                var isKeyDown = wParam == (IntPtr)WmKeyDown || wParam == (IntPtr)WmSysKeyDown;
                if (isKeyDown)
                {
                    var isRecordingAttempt = blockGameBarRecording || blockGameBarOpen;
                    var action = blockPrintScreen
                        ? "print-screen"
                        : blockSnipping
                            ? "snipping-shortcut"
                            : blockGameBarRecording
                                ? "game-bar-recording-shortcut"
                                : "game-bar-shortcut";

                    _statusCallback(action switch
                    {
                        "print-screen" => "Print Screen was blocked.",
                        "snipping-shortcut" => "Windows + Shift + S was blocked.",
                        "game-bar-recording-shortcut" => "Windows + Alt + R was blocked.",
                        _ => "Windows Game Bar was blocked."
                    });

                    _alertCallback(
                        isRecordingAttempt ? "Screen recording blocked" : "Screenshot blocked",
                        action switch
                        {
                            "print-screen" => "The Print Screen key is disabled by company security policy.",
                            "snipping-shortcut" => "Windows screen snipping is disabled by company security policy.",
                            "game-bar-recording-shortcut" => "Windows Game Bar screen recording is disabled by company security policy.",
                            _ => "Windows Game Bar is disabled while screen-recording protection is active."
                        });

                    var auditEvent = new AuditEvent
                    {
                        ActionKey = isRecordingAttempt ? ActionKeys.ScreenRecording : ActionKeys.ScreenCapture,
                        EventType = isRecordingAttempt ? "ScreenRecordingBlocked" : "ScreenshotBlocked",
                        Action = action,
                        Method = action switch
                        {
                            "print-screen" => "PrintScreen",
                            "snipping-shortcut" => "WindowsSnippingShortcut",
                            "game-bar-recording-shortcut" => "XboxGameBarRecordingShortcut",
                            _ => "XboxGameBarShortcut"
                        },
                        Result = "blocked",
                        ReasonCode = "DeniedByEffectiveScreenPolicy",
                        SourceProcessName = Environment.ProcessPath is null ? "CompanyDlp.Desktop" : Path.GetFileName(Environment.ProcessPath),
                        SourceProcessPath = Environment.ProcessPath ?? "",
                        SourceProcessId = Environment.ProcessId
                    };

                    // Dispatched via Task.Run rather than called directly: this method runs on the thread
                    // that owns the WH_KEYBOARD_LL hook (the app's UI thread, invoked synchronously by
                    // Windows), and starting the pipe I/O directly here ties its continuation to whatever
                    // SynchronizationContext/message-pump state that hook invocation left behind — which is
                    // exactly why this audit send was silently never reaching the outbox (confirmed live: a
                    // real block happened, but zero .evt files were ever written). Task.Run moves the actual
                    // send onto a thread-pool thread, and the response is now actually observed instead of a
                    // bare fire-and-forget `_ = ...` that discarded even a hard failure.
                    _ = SendAuditAsync(auditEvent);
                }
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private Task SendAuditAsync(AuditEvent auditEvent) => Task.Run(async () =>
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var response = await _pipeClient.SendAsync(DlpMessageTypes.Audit, auditEvent);
                if (response.Success) return;
                if (attempt == 1) await Task.Delay(250);
            }
            catch
            {
                if (attempt == 1) await Task.Delay(250);
            }
        }
        // Both attempts failed to reach the service pipe; surface it locally so it is at least
        // discoverable instead of vanishing the way the original fire-and-forget call did.
        _statusCallback($"Could not record the {auditEvent.ActionKey} block to the audit log.");
    });

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
