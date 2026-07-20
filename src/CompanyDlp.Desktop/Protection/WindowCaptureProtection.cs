using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CompanyDlp.Desktop.Protection;

public static class WindowCaptureProtection
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    public static bool ExcludeFromCapture(Window window)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
