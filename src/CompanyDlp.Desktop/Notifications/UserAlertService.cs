using System.Windows;
using System.Windows.Controls;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using System.Windows.Threading;
using CompanyDlp.Contracts;

namespace CompanyDlp.Desktop.Notifications;

public sealed class UserAlertService : IDisposable
{
    private readonly List<Window> _activeAlerts = [];
    private readonly Dictionary<string, DateTimeOffset> _recentAlerts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(3);
    private bool _disposed;

    public void Show(
        string title,
        string message,
        string severity = "Error",
        int durationSeconds = 6)
    {
        if (_disposed) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _ = dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            ShowOnUiThread(title, message, severity, durationSeconds);
        });
    }

    public void Show(UserNotification notification, int durationSeconds = 6) =>
        Show(notification.Title, notification.Message, notification.Severity, durationSeconds);

    private void ShowOnUiThread(string title, string message, string severity, int durationSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var key = BuildDeduplicationKey(title, message, severity);
        if (_recentAlerts.TryGetValue(key, out var lastShown) && now - lastShown < DuplicateWindow)
        {
            return;
        }
        _recentAlerts[key] = now;
        foreach (var expiredKey in _recentAlerts
                     .Where(item => now - item.Value > TimeSpan.FromMinutes(1))
                     .Select(item => item.Key)
                     .ToList())
        {
            _recentAlerts.Remove(expiredKey);
        }

        var background = severity.Equals("Warning", StringComparison.OrdinalIgnoreCase)
            ? WpfColor.FromRgb(180, 83, 9)
            : severity.Equals("Info", StringComparison.OrdinalIgnoreCase)
                ? WpfColor.FromRgb(30, 64, 175)
                : WpfColor.FromRgb(185, 28, 28);

        var window = new Window
        {
            Width = 430,
            Height = 142,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = WpfBrushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false
        };

        var border = new Border
        {
            Background = new WpfSolidColorBrush(background),
            BorderBrush = new WpfSolidColorBrush(WpfColor.FromArgb(170, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(18, 15, 18, 13),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.45,
                Color = WpfColors.Black
            }
        };

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = WpfBrushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = WpfBrushes.White,
            FontSize = 14,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 55
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Company DLP • Action blocked by company security policy",
            Foreground = new WpfSolidColorBrush(WpfColor.FromArgb(210, 255, 255, 255)),
            FontSize = 11,
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        border.Child = panel;
        window.Content = border;
        window.Opacity = 0.98;
        window.Closed += (_, _) =>
        {
            _activeAlerts.Remove(window);
            RepositionAlerts();
        };

        _activeAlerts.Add(window);
        RepositionAlerts();
        window.Show();

        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(durationSeconds, 2, 30))
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (window.IsVisible) window.Close();
        };
        timer.Start();
    }

    private static string BuildDeduplicationKey(string title, string message, string severity)
    {
        var text = $"{title} {message}".ToLowerInvariant();
        var category = text switch
        {
            var value when value.Contains("screen recording") || value.Contains("screen-recorder") => "screen-recording",
            var value when value.Contains("screenshot") || value.Contains("screen capture") || value.Contains("snipping") => "screenshot",
            var value when value.Contains("drag and drop") || value.Contains("dropped file") || value.Contains("dragging files") => "browser-file-drop",
            var value when value.Contains("file upload") || value.Contains("uploading files") || value.Contains("file picker") || value.Contains("selected file") => "browser-file-upload",
            var value when value.Contains("download") => "browser-download",
            var value when value.Contains("clipboard") || value.Contains("copy blocked") || value.Contains("paste blocked") => "clipboard",
            var value when value.Contains("usb") => "usb",
            _ => ""
        };
        return string.IsNullOrEmpty(category)
            ? string.Join('|', title, message, severity)
            : category;
    }

    private void RepositionAlerts()
    {
        var area = SystemParameters.WorkArea;
        for (var index = 0; index < _activeAlerts.Count; index++)
        {
            var alert = _activeAlerts[index];
            alert.Left = area.Right - alert.Width - 18;
            alert.Top = area.Top + 18 + index * (alert.Height + 12);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        _ = dispatcher.InvokeAsync(() =>
        {
            foreach (var alert in _activeAlerts.ToList())
            {
                alert.Close();
            }
            _activeAlerts.Clear();
        });
    }
}
