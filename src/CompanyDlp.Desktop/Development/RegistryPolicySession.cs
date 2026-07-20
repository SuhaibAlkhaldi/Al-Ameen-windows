using System.IO;
using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Desktop.Development;

public sealed class RegistryPolicySession
{
    private static readonly PolicyValue[] Values =
    [
        new(@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "BrowserGuestModeEnabled"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "DisableScreenshots"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "WebCaptureEnabled"),
        new(@"SOFTWARE\Policies\Microsoft\Edge", "DownloadRestrictions"),
        new(@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability"),
        new(@"SOFTWARE\Policies\Google\Chrome", "BrowserGuestModeEnabled"),
        new(@"SOFTWARE\Policies\Google\Chrome", "DownloadRestrictions"),
        new($@"SOFTWARE\Policies\Google\Chrome\3rdparty\extensions\{DevelopmentSessionManager.DevelopmentExtensionId}\policy", "developmentHttpFallback"),
        new($@"SOFTWARE\Policies\Microsoft\Edge\3rdparty\extensions\{DevelopmentSessionManager.DevelopmentExtensionId}\policy", "developmentHttpFallback"),
        new(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        new(@"SYSTEM\GameConfigStore", "GameDVR_Enabled")
    ];

    public string SessionFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanyDlp", "Development", "active-test-session.json");

    public bool HasActiveSession => File.Exists(SessionFilePath);

    public void Start(DlpPolicy policy)
    {
        if (HasActiveSession) Restore();
        var snapshots = Values.Select(ReadSnapshot).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
        File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(snapshots, JsonDefaults.Options));

        WriteDword(@"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability", policy.Browser.DisableIncognito ? 1 : null);
        WriteDword(@"SOFTWARE\Policies\Microsoft\Edge", "BrowserGuestModeEnabled", policy.Browser.DisableGuestMode ? 0 : null);
        WriteDword(@"SOFTWARE\Policies\Microsoft\Edge", "DisableScreenshots", policy.Browser.DisableBrowserScreenshots ? 1 : null);
        WriteDword(@"SOFTWARE\Policies\Microsoft\Edge", "WebCaptureEnabled", policy.Browser.DisableBrowserScreenshots ? 0 : null);
        WriteDword(@"SOFTWARE\Policies\Microsoft\Edge", "DownloadRestrictions", policy.Browser.BlockDownloads ? 3 : null);

        WriteDword(@"SOFTWARE\Policies\Google\Chrome", "IncognitoModeAvailability", policy.Browser.DisableIncognito ? 1 : null);
        WriteDword(@"SOFTWARE\Policies\Google\Chrome", "BrowserGuestModeEnabled", policy.Browser.DisableGuestMode ? 0 : null);
        WriteDword(@"SOFTWARE\Policies\Google\Chrome", "DownloadRestrictions", policy.Browser.BlockDownloads ? 3 : null);

        WriteDword(
            $@"SOFTWARE\Policies\Google\Chrome\3rdparty\extensions\{DevelopmentSessionManager.DevelopmentExtensionId}\policy",
            "developmentHttpFallback",
            1);
        WriteDword(
            $@"SOFTWARE\Policies\Microsoft\Edge\3rdparty\extensions\{DevelopmentSessionManager.DevelopmentExtensionId}\policy",
            "developmentHttpFallback",
            1);

        var disableGameCapture = policy.Screen.Enabled && policy.Screen.DisableWindowsGameCapture;
        WriteDword(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", disableGameCapture ? 0 : null);
        WriteDword(@"SYSTEM\GameConfigStore", "GameDVR_Enabled", disableGameCapture ? 0 : null);
    }

    public void Restore()
    {
        if (!File.Exists(SessionFilePath)) return;
        try
        {
            var snapshots = JsonSerializer.Deserialize<List<RegistrySnapshot>>(File.ReadAllText(SessionFilePath), JsonDefaults.Options) ?? [];
            foreach (var snapshot in snapshots)
            {
                using var key = Registry.CurrentUser.CreateSubKey(snapshot.Path, true);
                if (key is null) continue;
                if (!snapshot.Existed) key.DeleteValue(snapshot.Name, false);
                else if (snapshot.DwordValue.HasValue) key.SetValue(snapshot.Name, snapshot.DwordValue.Value, RegistryValueKind.DWord);
            }
        }
        finally
        {
            File.Delete(SessionFilePath);
        }
    }

    private static RegistrySnapshot ReadSnapshot(PolicyValue item)
    {
        using var key = Registry.CurrentUser.OpenSubKey(item.Path, false);
        var value = key?.GetValue(item.Name);
        return new RegistrySnapshot
        {
            Path = item.Path,
            Name = item.Name,
            Existed = value is not null,
            DwordValue = value is null ? null : Convert.ToInt32(value)
        };
    }

    private static void WriteDword(string path, string name, int? value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, true)
            ?? throw new InvalidOperationException($"Cannot open HKCU\\{path}");
        if (value.HasValue) key.SetValue(name, value.Value, RegistryValueKind.DWord);
        else key.DeleteValue(name, false);
    }

    private sealed record PolicyValue(string Path, string Name);

    public sealed class RegistrySnapshot
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public int? DwordValue { get; set; }
    }
}
