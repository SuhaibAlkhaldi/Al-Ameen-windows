using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

public sealed record FileClassificationStatusEntry(
    string Path,
    string Status,
    string? LastClassifiedHash,
    DateTimeOffset? LastScannedAtUtc,
    string? LastReasonCode,
    DateTimeOffset UpdatedAtUtc);

// Local, persisted PATH -> display status lookup, written by FileInventoryScanner and read by
// FileClassificationStatusResolver for the Explorer hover-tooltip feature (CompanyDlp.ShellExtension).
// This is purely additive and display-only - it exists because FileClassificationCache is keyed by
// content hash alone, with no way to ask "what's the status for path X" without already knowing its
// current hash, which can't express Not Scanned/Pending/Reclassification Required for a given path.
// Never read by PermissionEvaluator; enforcement continues to work exclusively off
// FileClassificationCache via a live-computed hash, unaffected by this store.
public sealed class FileClassificationStatusStore(PolicyStore policyStore, ILogger<FileClassificationStatusStore> logger)
{
    private readonly object _sync = new();
    private Dictionary<string, FileClassificationStatusEntry>? _entries;

    // Case-insensitive, full-path form so the same file is matched regardless of how its path was
    // spelled (relative segments, drive-letter casing, trailing separators) by different callers
    // (the scanner's own enumeration vs. a path Explorer hands to the shell extension).
    public static string NormalizePath(string path) => Path.GetFullPath(path);

    public FileClassificationStatusEntry? TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_sync)
        {
            EnsureLoaded();
            return _entries!.GetValueOrDefault(NormalizePath(path));
        }
    }

    public void Set(FileClassificationStatusEntry entry)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _entries![NormalizePath(entry.Path)] = entry;
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_entries is not null) return;

        var path = GetStorePath();
        try
        {
            if (File.Exists(path))
            {
                var values = JsonSerializer.Deserialize<List<FileClassificationStatusEntry>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
                _entries = values.ToDictionary(item => NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the file classification status store; starting empty.");
        }

        _entries = new Dictionary<string, FileClassificationStatusEntry>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            var path = GetStorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries!.Values, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the file classification status store.");
        }
    }

    private string GetStorePath() => Path.Combine(GetRoot(), "file-classification-status.json");

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
