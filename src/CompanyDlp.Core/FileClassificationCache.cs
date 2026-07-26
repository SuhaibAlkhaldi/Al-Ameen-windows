using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

public sealed record CachedFileClassification(
    string FileHash,
    string Classification,
    string ReasonCode,
    DateTimeOffset ClassifiedAtUtc);

// Local, persisted hash -> classification lookup, written by the background inventory scanner
// (CompanyDlp.Service) and read by PermissionEvaluator so that enforcement at upload/drag-drop
// time is always a fast local lookup, never a live AI call. A file with no entry here is treated
// as unclassified by callers - see PermissionEvaluator, which falls back to ClassificationTiers.
// VerySecret (fail-closed) for that case, not this class's job to decide.
public sealed class FileClassificationCache(PolicyStore policyStore, ILogger<FileClassificationCache> logger)
{
    private readonly object _sync = new();
    private Dictionary<string, CachedFileClassification>? _entries;

    public CachedFileClassification? TryGet(string fileHash)
    {
        if (string.IsNullOrWhiteSpace(fileHash)) return null;
        lock (_sync)
        {
            EnsureLoaded();
            return _entries!.GetValueOrDefault(fileHash);
        }
    }

    public void Set(CachedFileClassification result)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _entries![result.FileHash] = result;
            Save();
        }
    }

    public bool BackfillCompleted
    {
        get
        {
            lock (_sync)
            {
                EnsureLoaded();
                return File.Exists(GetBackfillMarkerPath());
            }
        }
        set
        {
            lock (_sync)
            {
                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GetBackfillMarkerPath())!);
                    File.WriteAllText(GetBackfillMarkerPath(), DateTimeOffset.UtcNow.ToString("O"));
                }
                else
                {
                    try { File.Delete(GetBackfillMarkerPath()); } catch { }
                }
            }
        }
    }

    private void EnsureLoaded()
    {
        if (_entries is not null) return;

        var path = GetCachePath();
        try
        {
            if (File.Exists(path))
            {
                var values = JsonSerializer.Deserialize<List<CachedFileClassification>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
                _entries = values.ToDictionary(item => item.FileHash, StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the file classification cache; starting empty.");
        }

        _entries = new Dictionary<string, CachedFileClassification>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            var path = GetCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_entries!.Values, JsonDefaults.Options));
            File.Move(temporary, path, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the file classification cache.");
        }
    }

    private string GetCachePath() => Path.Combine(GetRoot(), "file-classification-cache.json");
    private string GetBackfillMarkerPath() => Path.Combine(GetRoot(), "file-classification-backfill.marker");

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
