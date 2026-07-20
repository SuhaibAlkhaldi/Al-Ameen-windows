using System.IO;
using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Win32;

namespace CompanyDlp.Desktop.Development;

public sealed class NativeHostRegistrySession
{
    private static readonly string[] Paths =
    [
        @"SOFTWARE\Google\Chrome\NativeMessagingHosts\com.company.dlp",
        @"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.company.dlp",
        @"SOFTWARE\Mozilla\NativeMessagingHosts\com.company.dlp"
    ];

    public string BackupPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CompanyDlp", "Development", "native-host-backup.json");

    public bool HasBackup => File.Exists(BackupPath);

    public void Start(string chromiumManifestPath, string firefoxManifestPath)
    {
        var snapshots = Paths.Select(path =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(path, false);
            var value = key?.GetValue(null) as string;
            return new NativeHostSnapshot { Path = path, Existed = value is not null, Value = value ?? "" };
        }).ToList();

        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(snapshots, JsonDefaults.Options));

        foreach (var path in Paths)
        {
            using var key = Registry.CurrentUser.CreateSubKey(path, true);
            key?.SetValue(
                null,
                path.Contains(@"SOFTWARE\Mozilla\", StringComparison.OrdinalIgnoreCase)
                    ? firefoxManifestPath
                    : chromiumManifestPath,
                RegistryValueKind.String);
        }
    }

    public void Restore()
    {
        if (!File.Exists(BackupPath))
        {
            foreach (var path in Paths)
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(path, false); } catch { }
            }
            return;
        }

        try
        {
            var snapshots = JsonSerializer.Deserialize<List<NativeHostSnapshot>>(File.ReadAllText(BackupPath), JsonDefaults.Options) ?? [];
            foreach (var snapshot in snapshots)
            {
                if (!snapshot.Existed)
                {
                    try { Registry.CurrentUser.DeleteSubKeyTree(snapshot.Path, false); } catch { }
                    continue;
                }

                using var key = Registry.CurrentUser.CreateSubKey(snapshot.Path, true);
                key?.SetValue(null, snapshot.Value, RegistryValueKind.String);
            }
        }
        finally
        {
            File.Delete(BackupPath);
        }
    }

    public sealed class NativeHostSnapshot
    {
        public string Path { get; set; } = "";
        public bool Existed { get; set; }
        public string Value { get; set; } = "";
    }
}
