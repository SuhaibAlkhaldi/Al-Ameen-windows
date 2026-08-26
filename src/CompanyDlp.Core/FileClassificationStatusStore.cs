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
    DateTimeOffset UpdatedAtUtc,
    // Mirrors FileInventoryScanner's in-memory _lastSeenWriteTimes (the file's own LastWriteTimeUtc
    // as of the last time this scanner fully finished with it, including any watermark rewrite) -
    // persisted so it survives a service restart. This can't be reconstructed from LastClassifiedHash:
    // watermarking rewrites the file's bytes, so re-hashing it after a restart never reproduces the
    // pre-watermark hash that was actually classified - confirmed live, that approach just re-stamped
    // the file every restart forever. The write-time comparison sidesteps hashing entirely for a file
    // the scanner has already fully handled and nothing has touched since.
    DateTimeOffset? LastSeenWriteTimeUtc = null);

// Local, persisted PATH -> display status lookup, written by FileInventoryScanner and read by
// FileClassificationStatusResolver for the Explorer hover-tooltip feature (CompanyDlp.ShellExtension).
// This is purely additive and display-only - it exists because FileClassificationCache is keyed by
// content hash alone, with no way to ask "what's the status for path X" without already knowing its
// current hash, which can't express Not Scanned/Pending/Reclassification Required for a given path.
// Never read by PermissionEvaluator; enforcement continues to work exclusively off
// FileClassificationCache via a live-computed hash, unaffected by this store.
public sealed class FileClassificationStatusStore(PolicyStore policyStore, ILogger<FileClassificationStatusStore> logger)
{
    // Confirmed live 2026-08-26: Set() used to call Save() synchronously on every single call, and
    // Save() always serializes+writes the ENTIRE dictionary (not just the changed entry). A
    // background scan walking tens of thousands of files therefore did tens of thousands of full-
    // dictionary rewrites, each one slower than the last as the dictionary grew - on a Desktop with
    // ~66,000 files this made the scanner's real, measured CPU usage (confirmed via Get-Process,
    // ~7.5% sustained) produce zero visible progress for many minutes, because nearly all of that
    // work was rewriting the same growing JSON blob over and over rather than doing anything new.
    // This store is purely a display cache (see the class comment above - PermissionEvaluator never
    // reads it), so losing the last couple of seconds of updates in a crash is an acceptable
    // trade-off for not serializing the whole file on every single classified item.
    private static readonly TimeSpan SaveThrottleInterval = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private Dictionary<string, FileClassificationStatusEntry>? _entries;
    private bool _dirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;

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

    // Used once, at startup, by FileInventoryScanner to rebuild its in-memory _lastSeenWriteTimes
    // guard from the persisted LastSeenWriteTimeUtc values - see that field's comment.
    public IReadOnlyCollection<FileClassificationStatusEntry> GetAll()
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _entries!.Values.ToList();
        }
    }

    public void Set(FileClassificationStatusEntry entry)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _entries![NormalizePath(entry.Path)] = entry;
            SaveThrottled();
        }
    }

    // Used when a path stops being valid without a replacement entry taking its place - currently
    // only by FileInventoryScanner after a filename-tagging rename, to drop the old path's entry
    // once a fresh one is written under the new path (see ApplyFilenameTag). Without this, a renamed
    // file would leave a stale, permanently-orphaned entry behind under its old name. Filename tagging
    // is off by default and this only fires on an actual rename, so it stays immediate/un-throttled -
    // it was never the source of the write-storm Set() had.
    public void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_sync)
        {
            EnsureLoaded();
            if (_entries!.Remove(NormalizePath(path))) Save();
        }
    }

    // Forces any pending throttled write out to disk immediately - called by FileInventoryScanner
    // once at the end of every TickAsync pass so a tick's last handful of updates (the ones that
    // landed inside the most recent SaveThrottleInterval window) are never left sitting in memory
    // indefinitely if the next tick is delayed or the service stops.
    public void Flush()
    {
        lock (_sync)
        {
            if (_dirty) Save();
        }
    }

    // Always updates the in-memory dictionary the instant Set()/Delete() is called (TryGet/GetAll
    // are correct immediately either way) - only the disk WRITE is deferred, and only while updates
    // are arriving faster than SaveThrottleInterval. The first Set() after a quiet period still
    // writes immediately (DateTime.MinValue default plus this being the first call after Flush()
    // resets _dirty makes the elapsed-time check pass), so a single isolated classification (the
    // common case outside a bulk background scan) is never delayed.
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
            _dirty = false;
            _lastSaveUtc = DateTime.UtcNow;
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
