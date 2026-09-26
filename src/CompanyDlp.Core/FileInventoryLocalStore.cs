using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

// The agent's own belief about what it last told the backend's FileInventoryRecords table about
// each watched-folder path - NOT a duplicate of FileClassificationCache (that's hash -> classification;
// this is path -> "last synced state", the same shape as the central table's current-state row). Used
// by FileInventoryChangeWatcher to know whether a detected event is genuinely new, and by
// FileInventoryReconciliationRunner to diff a fresh disk walk against what was last reported.
public sealed record FileInventoryLocalEntry(
    string FilePath,
    string? FileHash,
    long SizeBytes,
    string ClassificationTier,
    string Provenance,
    bool IsProtected,
    DateTimeOffset LastWriteTimeUtc,
    DateTimeOffset LastSyncedAtUtc);

public sealed class FileInventoryLocalStore(PolicyStore policyStore, ILogger<FileInventoryLocalStore> logger)
{
    // Same write-storm rationale as FileClassificationStatusStore (see that class's comment) - the
    // initial full sync and every reconciliation walk touch every watched-folder file in one pass, so
    // an un-throttled Save() per Set() would be the identical O(n^2) full-dictionary-rewrite problem
    // confirmed live there on a ~66,000-file Desktop. This store is a local sync-state cache the
    // backend is the real source of truth for (a lost update here is corrected by the next
    // Reconciliation pass, never silently permanent) - the same acceptable trade-off.
    private static readonly TimeSpan SaveThrottleInterval = TimeSpan.FromSeconds(2);

    private readonly object _sync = new();
    private Dictionary<string, FileInventoryLocalEntry>? _entries;
    private bool _dirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public static string NormalizePath(string path) => Path.GetFullPath(path);

    public FileInventoryLocalEntry? TryGet(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (_sync)
        {
            EnsureLoaded();
            return _entries!.GetValueOrDefault(NormalizePath(path));
        }
    }

    // Used by FileInventoryReconciliationRunner to diff a fresh disk walk against everything the agent
    // last believed it had synced.
    public IReadOnlyDictionary<string, FileInventoryLocalEntry> GetAll()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return new Dictionary<string, FileInventoryLocalEntry>(_entries!, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Set(FileInventoryLocalEntry entry)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _entries![NormalizePath(entry.FilePath)] = entry;
            SaveThrottled();
        }
    }

    // Un-throttled, unlike Set() - a delete/rename is a discrete, infrequent event (not part of a
    // bulk walk), and losing track of "this path is gone" until the next throttle window would let a
    // stale entry keep being treated as still-present in the meantime.
    public void Remove(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_sync)
        {
            EnsureLoaded();
            if (_entries!.Remove(NormalizePath(path))) Save();
        }
    }

    // Called at the end of every bulk pass (initial sync, reconciliation) so the last handful of
    // throttled updates are never left sitting in memory indefinitely - same convention as
    // FileClassificationStatusStore.Flush().
    public void Flush()
    {
        lock (_sync)
        {
            if (_dirty) Save();
        }
    }

    // "Has the agent ever completed a full initial walk-and-sync of the watched folders" - same
    // marker-file pattern as FileClassificationCache.BackfillCompleted (presence of the file, whose
    // content is just a timestamp for diagnostics, is the boolean; see that class's comment for why a
    // marker file rather than a field in the JSON payload). FileInventorySyncWorker checks this once
    // on startup to decide whether to run FileInventoryInitialSyncRunner before its normal incremental
    // drain loop.
    public bool InitialSyncCompleted
    {
        get
        {
            lock (_sync)
            {
                return File.Exists(GetInitialSyncMarkerPath());
            }
        }
        set
        {
            lock (_sync)
            {
                if (value)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(GetInitialSyncMarkerPath())!);
                    File.WriteAllText(GetInitialSyncMarkerPath(), DateTimeOffset.UtcNow.ToString("O"));
                }
                else
                {
                    try { File.Delete(GetInitialSyncMarkerPath()); } catch { }
                }
            }
        }
    }

    private void SaveThrottled()
    {
        _dirty = true;
        if (DateTime.UtcNow - _lastSaveUtc < SaveThrottleInterval) return;
        Save();
    }

    private void EnsureLoaded()
    {
        if (_entries is not null) return;

        var path = GetStorePath();
        try
        {
            if (File.Exists(path))
            {
                var values = JsonSerializer.Deserialize<List<FileInventoryLocalEntry>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
                _entries = values.ToDictionary(item => NormalizePath(item.FilePath), StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the file inventory local state store; starting empty.");
        }

        _entries = new Dictionary<string, FileInventoryLocalEntry>(StringComparer.OrdinalIgnoreCase);
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
            _dirty = false;
            _lastSaveUtc = DateTime.UtcNow;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the file inventory local state store.");
        }
    }

    // Subfolder, not a flat file next to file-classification-cache.json - matches FileProvenanceStore's
    // convention (Path.Combine(GetRoot(), "FileProvenance", "provenance.json")) for a store that isn't
    // just a single flat lookup.
    private string GetStorePath() => Path.Combine(GetRoot(), "FileInventory", "file-inventory-local-state.json");
    private string GetInitialSyncMarkerPath() => Path.Combine(GetRoot(), "FileInventory", "initial-sync.marker");

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
