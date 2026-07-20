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

    private readonly WatermarkPolicy _policy;
    private readonly DispatcherTimer _timer;

    public WatermarkWindow(WatermarkPolicy policy, string sessionId)
    {
        _policy = policy;
        _ = sessionId; // Kept for API compatibility; session id is intentionally not displayed.

        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += (_, _) => RenderWatermarks();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RenderWatermarks();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);

        RenderWatermarks();
        _timer.Start();
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
}
