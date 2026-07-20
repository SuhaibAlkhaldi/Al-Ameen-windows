using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class UsbBaselineStore(PolicyStore policyStore, ILogger<UsbBaselineStore> logger)
{
    private readonly object _sync = new();
    private HashSet<string>? _trusted;

    public IReadOnlySet<string> GetOrCreate(IEnumerable<UsbDeviceBundleInfo> current)
    {
        lock (_sync)
        {
            if (_trusted is not null) return _trusted;
            var path = GetPath();
            try
            {
                if (File.Exists(path))
                {
                    var values = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
                    _trusted = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                    return _trusted;
                }

                _trusted = policyStore.Get().Usb.TrustDevicesPresentAtFirstRun
                    ? new HashSet<string>(current.Select(item => item.RootInstanceId), StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Save(path, _trusted);
                logger.LogWarning("Created USB baseline with {Count} device roots. Disconnect unapproved external devices before first production start.", _trusted.Count);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Unable to load or create USB baseline.");
                _trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return _trusted;
        }
    }

    public void Reset(IEnumerable<UsbDeviceBundleInfo> current)
    {
        lock (_sync)
        {
            _trusted = new HashSet<string>(current.Select(item => item.RootInstanceId), StringComparer.OrdinalIgnoreCase);
            Save(GetPath(), _trusted);
        }
    }

    private string GetPath()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "usb-baseline.json");
    }

    private static void Save(string path, IEnumerable<string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(values.OrderBy(value => value), JsonDefaults.Options));
    }
}
