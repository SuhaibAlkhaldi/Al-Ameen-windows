using System.IO;
using System.Diagnostics;
using System.Management;
using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Desktop.Development;

public sealed class DevelopmentSessionManager
{
    public const string DevelopmentExtensionId = "ndbbpeagkbfbkmgdklpphomiolnmbhhi";
    private readonly RegistryPolicySession _registrySession = new();
    private readonly NativeHostRegistrySession _nativeHostSession = new();
    private readonly List<Process> _launched = [];
    private DevelopmentBrowserTestServer? _testServer;
    private string? _profileRoot;

    public bool HasUncleanSession => _registrySession.HasActiveSession || _nativeHostSession.HasBackup;

    public void RecoverIfNeeded()
    {
        if (!HasUncleanSession) return;
        Restore();
    }

    public void Start(DlpPolicy policy)
    {
        if (!policy.Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Temporary test sessions are only allowed in Development mode.");
        }

        try
        {
            _registrySession.Start(policy);
            RegisterNativeHost();
            _profileRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CompanyDlp", "Development", "BrowserProfiles", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(_profileRoot);
            _testServer = new DevelopmentBrowserTestServer();
            _testServer.Start();
        }
        catch
        {
            Restore();
            throw;
        }
    }

    public string LaunchProtectedEdge()
    {
        EnsureStarted();
        var browser = FindEdge() ?? throw new FileNotFoundException("Microsoft Edge was not found.");
        return Launch(browser, Path.Combine(_profileRoot!, "Edge"));
    }

    public string LaunchProtectedChrome()
    {
        EnsureStarted();
        var browser = FindChrome() ?? throw new FileNotFoundException("Google Chrome was not found.");
        return Launch(browser, Path.Combine(_profileRoot!, "Chrome"));
    }

    public string LaunchPreferredProtectedBrowser()
    {
        EnsureStarted();

        var chrome = FindChrome();
        if (!string.IsNullOrWhiteSpace(chrome))
        {
            var profile = Launch(chrome, Path.Combine(_profileRoot!, "Chrome"));
            return $"Protected Chrome launched automatically. Test uploads and drag/drop only in this protected browser window. Profile: {profile}";
        }

        var edge = FindEdge();
        if (!string.IsNullOrWhiteSpace(edge))
        {
            var profile = Launch(edge, Path.Combine(_profileRoot!, "Edge"));
            return $"Protected Edge launched automatically. Test uploads and drag/drop only in this protected browser window. Profile: {profile}";
        }

        throw new FileNotFoundException("Neither Google Chrome nor Microsoft Edge was found.");
    }

    public void Restore()
    {
        CloseTestBrowsers();
        _registrySession.Restore();
        _nativeHostSession.Restore();
        if (!string.IsNullOrWhiteSpace(_profileRoot))
        {
            try { Directory.Delete(_profileRoot, true); } catch { }
        }
        _profileRoot = null;
        _testServer?.Dispose();
        _testServer = null;
    }

    private string Launch(string browserPath, string profilePath)
    {
        var extensionPath = ResolveExtensionPath();
        Directory.CreateDirectory(profilePath);
        var arguments = string.Join(' ', new[]
        {
            $"--user-data-dir=\"{profilePath}\"",
            $"--load-extension=\"{extensionPath}\"",
            $"--disable-extensions-except=\"{extensionPath}\"",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
            _testServer?.Url ?? "https://example.com/?company-dlp-protected=1"
        });
        var process = Process.Start(new ProcessStartInfo(browserPath, arguments) { UseShellExecute = true });
        if (process is not null) _launched.Add(process);
        return profilePath;
    }

    private void CloseTestBrowsers()
    {
        foreach (var process in _launched.ToList())
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            process.Dispose();
        }
        _launched.Clear();

        if (string.IsNullOrWhiteSpace(_profileRoot) || !OperatingSystem.IsWindows()) return;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='msedge.exe' OR Name='chrome.exe'");
            foreach (ManagementObject item in searcher.Get())
            {
                var commandLine = Convert.ToString(item["CommandLine"]) ?? "";
                if (!commandLine.Contains(_profileRoot, StringComparison.OrdinalIgnoreCase)) continue;
                var pid = Convert.ToInt32(item["ProcessId"]);
                try { Process.GetProcessById(pid).Kill(true); } catch { }
            }
        }
        catch { }
    }

    private void RegisterNativeHost()
    {
        var nativeHostExe = Environment.GetEnvironmentVariable("COMPANY_DLP_NATIVE_HOST_EXE");
        if (string.IsNullOrWhiteSpace(nativeHostExe) || !File.Exists(nativeHostExe))
        {
            throw new FileNotFoundException("COMPANY_DLP_NATIVE_HOST_EXE does not point to CompanyDlp.NativeHost.exe. Run scripts\\run-development.ps1.");
        }

        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanyDlp", "Development");
        Directory.CreateDirectory(directory);
        var chromiumManifestPath = Path.Combine(directory, "com.company.dlp.chromium.json");
        var chromiumManifest = new
        {
            name = "com.company.dlp",
            description = "Company DLP development native messaging host",
            path = nativeHostExe,
            type = "stdio",
            allowed_origins = new[] { $"chrome-extension://{DevelopmentExtensionId}/" }
        };
        File.WriteAllText(chromiumManifestPath, JsonSerializer.Serialize(chromiumManifest, JsonDefaults.Options));

        var firefoxManifestPath = Path.Combine(directory, "com.company.dlp.firefox.json");
        var firefoxManifest = new
        {
            name = "com.company.dlp",
            description = "Company DLP development native messaging host for Firefox",
            path = nativeHostExe,
            type = "stdio",
            allowed_extensions = new[] { "company-dlp@company.local" }
        };
        File.WriteAllText(firefoxManifestPath, JsonSerializer.Serialize(firefoxManifest, JsonDefaults.Options));

        _nativeHostSession.Start(chromiumManifestPath, firefoxManifestPath);
    }

    private static string ResolveExtensionPath()
    {
        var projectRoot = Environment.GetEnvironmentVariable("COMPANY_DLP_PROJECT_ROOT");
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            throw new InvalidOperationException("COMPANY_DLP_PROJECT_ROOT is missing. Start the project using scripts\\run-development.ps1.");
        }
        var extensionPath = Path.Combine(projectRoot, "browser-extension");
        if (!File.Exists(Path.Combine(extensionPath, "manifest.json")))
        {
            throw new DirectoryNotFoundException($"Browser extension not found: {extensionPath}");
        }
        return extensionPath;
    }

    private void EnsureStarted()
    {
        if (!_registrySession.HasActiveSession || string.IsNullOrWhiteSpace(_profileRoot))
        {
            throw new InvalidOperationException("Start a development test session first.");
        }
    }

    private static string? FindEdge() => FindBrowser("Microsoft\\Edge\\Application\\msedge.exe", "msedge.exe");
    private static string? FindChrome() => FindBrowser("Google\\Chrome\\Application\\chrome.exe", "chrome.exe");

    private static string? FindBrowser(string relativePath, string executable)
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        })
        {
            var candidate = Path.Combine(root, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        var fromPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Select(path => Path.Combine(path, executable))
            .FirstOrDefault(File.Exists);
        return fromPath;
    }
}
