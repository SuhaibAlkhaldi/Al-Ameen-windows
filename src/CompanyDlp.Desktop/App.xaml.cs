using System.Windows;
using CompanyDlp.Desktop.Crypto;

namespace CompanyDlp.Desktop;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (ShellCryptoCommandRunner.TryParse(e.Args, out var command))
        {
            var exitCode = await ShellCryptoCommandRunner.RunAsync(command);
            Shutdown(exitCode);
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
