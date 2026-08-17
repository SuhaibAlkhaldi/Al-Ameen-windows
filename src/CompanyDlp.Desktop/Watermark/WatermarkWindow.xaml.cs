using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CompanyDlp.Contracts;
using WpfColor = System.Windows.Media.Color;

namespace CompanyDlp.Desktop.Watermark;

public partial class WatermarkWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    // WPF's Topmost="True" (see WatermarkWindow.xaml) only issues a single SetWindowPos(HWND_TOPMOST)
    // call when the window is created/shown - it is a one-time placement, not a continuously enforced
    // Z-order rule. Windows' "always on top" band is ordered by most-recent HWND_TOPMOST call, not by
    // who asked first: any other topmost-capable window that gets (re)inserted into that band later -
    // confirmed live with Chrome, which churns its own Z-order on things like new tabs, the downloads
    // shelf, PiP, or entering/leaving fullscreen - can end up above the watermark permanently, since
    // nothing here ever asked to be put back on top again. The watermark then keeps rendering (the 1s
    // RenderWatermarks() timer never stopped), it is just no longer the topmost window, so it silently
    // stops being visible over that one application while still showing fine over the desktop (which
    // sits at the very bottom of the Z-order and never contests topmost placement).
    // Fix: re-assert HWND_TOPMOST on the same 1s timer tick that already runs, not just once at Loaded.
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoActivate = 0x0010;

    private readonly WatermarkPolicy _policy;
    private readonly DispatcherTimer _timer;
    private IntPtr _handle;

    public WatermarkWindow(WatermarkPolicy policy, string sessionId)
    {
        _policy = policy;
        _ = sessionId; // Kept for API compatibility; session id is intentionally not displayed.

        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => RenderWatermarks();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            RenderWatermarks();
            ReassertTopmost();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(_handle, GwlExStyle);
        SetWindowLong(_handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);

        RenderWatermarks();
        ReassertTopmost();
        _timer.Start();
    }

    // SWP_NOACTIVATE is required here on top of the WS_EX_NOACTIVATE extended style already set above -
    // WS_EX_NOACTIVATE only stops the window from being activated by mouse/keyboard input, it has no
    // effect on an explicit SetWindowPos call, which would otherwise steal focus from whatever the
    // employee is actively typing into once a second.
    private void ReassertTopmost()
    {
        if (_handle == IntPtr.Zero) return;
        SetWindowPos(_handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void RenderWatermarks()
    {
        WatermarkCanvas.Children.Clear();

        var text = BuildText();
        var width = Math.Max(ActualWidth, 800);
        var height = Math.Max(ActualHeight, 600);
        var horizontalSpacing = Math.Max(420, _policy.HorizontalSpacing);
        var verticalSpacing = Math.Max(155, _policy.VerticalSpacing);
        var alpha = (byte)Math.Clamp(_policy.Opacity * 255, 28, 105);

        // Alternate the starting X position to create a clean staggered pattern.
        var row = 0;
        for (var y = -80; y < height + verticalSpacing; y += verticalSpacing, row++)
        {
            var rowOffset = row % 2 == 0 ? 0 : horizontalSpacing / 2;
            for (var x = -220 + rowOffset; x < width + horizontalSpacing; x += horizontalSpacing)
            {
                var label = new TextBlock
                {
                    Text = text,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI Semibold"),
                    FontSize = Math.Clamp(_policy.FontSize, 15, 24),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(WpfColor.FromArgb(alpha, 24, 24, 27)),
                    Effect = new DropShadowEffect
                    {
                        Color = WpfColor.FromArgb(150, 255, 255, 255),
                        Opacity = 0.75,
                        ShadowDepth = 0,
                        BlurRadius = 1.25
                    },
                    RenderTransform = new RotateTransform(-18),
                    RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, y);
                WatermarkCanvas.Children.Add(label);
            }
        }
    }

    private string BuildText()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_policy.Prefix)) parts.Add(_policy.Prefix.Trim());
        if (_policy.IncludeMachineName) parts.Add(Environment.MachineName);
        if (_policy.IncludeUsername) parts.Add(Environment.UserName);
        if (_policy.IncludeTime) parts.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return parts.Count == 0 ? $"{Environment.MachineName} - {DateTime.Now:yyyy-MM-dd HH:mm:ss}" : string.Join(" - ", parts);
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    // hwndInsertAfter is declared IntPtr, not int, deliberately - HWND is pointer-sized (8 bytes on
    // x64), and this agent always builds/runs as win-x64 (see publish.ps1's -r win-x64). Declaring it
    // as a 32-bit int here would under-marshal that parameter and misalign every argument after it in
    // the native call.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
