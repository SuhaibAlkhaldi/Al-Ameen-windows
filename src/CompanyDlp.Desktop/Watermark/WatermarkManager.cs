using WinFormsScreen = System.Windows.Forms.Screen;
using CompanyDlp.Contracts;

namespace CompanyDlp.Desktop.Watermark;

public sealed class WatermarkManager(WatermarkPolicy policy) : IDisposable
{
    private readonly List<WatermarkWindow> _windows = [];
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    public void Start()
    {
        if (_windows.Count > 0) return;
        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var bounds = screen.Bounds;
            var window = new WatermarkWindow(policy, _sessionId)
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height
            };
            window.Show();
            _windows.Add(window);
        }
    }

    public void Dispose()
    {
        foreach (var window in _windows.ToList()) window.Close();
        _windows.Clear();
    }
}
