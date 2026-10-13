namespace CompanyDlp.Service;

// Shared by every WebViewDesktopObserver (Phase 8: WhatsApp Desktop, Teams Desktop, and any future desktop channel
// whose recipient detection rides on WebView2's Chrome DevTools Protocol). See WebViewDesktopObserver's header
// comment for the full discovery story; this class holds only the two pieces that are NOT per-app:
//
//   1. WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS is a single environment variable shared by every WebView2-hosted app
//      running as this Windows user - it is NOT per-app. Confirmed live (2026-10-13): running WhatsApp Desktop and
//      the new Microsoft Teams at the same time with this set to a FIXED port ("--remote-debugging-port=9333")
//      made both of them try to bind that same port; they happened to land on different address families (one on
//      127.0.0.1, one on [::1]) and both "worked", but that split is an accident of bind order, not a guarantee -
//      a third concurrent app, or the same two apps starting in the other order, would leave one of them with no
//      working CDP port at all, failing silently.
//   2. The fix, also confirmed live: set the argument to "--remote-debugging-port=0" instead. Chromium (and so
//      WebView2) then binds an OS-assigned free port per browser process - no two WebView2 apps can ever collide,
//      no matter how many are running - and writes that actual port number to a "DevToolsActivePort" file inside
//      the browser's own user-data directory (its first line is the port; this class only needs that line). Each
//      packaged app's user-data directory lives under "%LocalAppData%\Packages\<PackageFamilyName>\...\EBWebView"
//      (confirmed the exact subpath differs per app - WhatsApp Desktop has it directly under LocalCache\EBWebView,
//      Microsoft Teams nests it one level deeper under LocalCache\Microsoft\MSTeams\EBWebView - so this searches
//      for the file by name under the package root instead of hardcoding either shape).
internal static class WebView2DebugPort
{
    private const string EnvVarName = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
    private const string DesiredValue = "--remote-debugging-port=0";

    // Idempotent and cheap to call every tick from every observer: only actually touches the registry (and
    // broadcasts the change) the first time, or if something else changed the value in between.
    public static void EnsureDynamicPortEnabled(ILogger logger)
    {
        try
        {
            var current = Environment.GetEnvironmentVariable(EnvVarName, EnvironmentVariableTarget.User);
            if (current == DesiredValue) return;

            Environment.SetEnvironmentVariable(EnvVarName, DesiredValue, EnvironmentVariableTarget.User);
            logger.LogInformation(
                "Set {EnvVar} to a dynamic CDP port for desktop recipient observation; takes effect the next time an " +
                "affected app is started.",
                EnvVarName);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not set {EnvVar}; desktop recipient observation cannot attach.", EnvVarName);
        }
    }

    // Returns null if the package's user-data directory, or a DevToolsActivePort file inside it, does not exist
    // yet (both normal while the app is still starting up) or cannot be parsed.
    public static int? TryGetActivePort(string packageFamilyName, ILogger logger)
    {
        try
        {
            var packageRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                packageFamilyName);
            if (!Directory.Exists(packageRoot)) return null;

            var portFile = Directory.EnumerateFiles(packageRoot, "DevToolsActivePort", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (portFile is null) return null;

            var firstLine = File.ReadLines(portFile).FirstOrDefault();
            return int.TryParse(firstLine, out var port) ? port : null;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not read the active CDP port for package {PackageFamilyName}.", packageFamilyName);
            return null;
        }
    }
}
