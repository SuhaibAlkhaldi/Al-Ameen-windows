using System.Text.Json;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

public static class FileProvenanceOrigins
{
    public const string Received = "Received";
    public const string SelfCreated = "SelfCreated";
}

public static class FileProvenanceChannels
{
    public const string BrowserDownload = "BrowserDownload";
    public const string Usb = "Usb";

    // Third channel: a file that landed via some other desktop application (Outlook, Teams, ...)
    // receiving it from the network, detected by DesktopAppProvenanceMonitor. Unlike the two channels
    // above, this one is a best-effort correlation heuristic rather than an exact per-file signal - see
    // that class's comment for why, and DlpPolicy.DesktopAppProvenance for why it defaults to off.
    public const string DesktopApp = "DesktopApp";
}

// One record per content hash (SHA-256, same key space as FileClassificationCache/EncryptedFileHashStore)
// that a known "received from outside" channel has positively matched - see ActionKeys.FileOpenAccess's
// comment for the full design. The ABSENCE of a record for a given hash is the default and means
// "treat as self-created, open freely" - there is no reliable positive signal for local authorship in
// user-mode (would need a kernel driver), so a record is only ever written for a positive match against
// one of the three channels this store currently tracks (browser download, USB copy, desktop
// application). Never overwritten once written for a given hash - the first channel to claim a hash
// wins, since a file's content once classified "received" never becomes "self-created" again just
// because it wasn't re-matched.
public sealed record FileProvenanceRecord(string ContentHash, string Origin, string DetectedChannel, DateTimeOffset DetectedAtUtc);

public sealed class FileProvenanceStore(PolicyStore policyStore, ILogger<FileProvenanceStore> logger)
{
    private readonly object _sync = new();
    private Dictionary<string, FileProvenanceRecord>? _records;

    private string RootDirectory => Path.Combine(GetRoot(), "FileProvenance");
    private string MetadataPath => Path.Combine(RootDirectory, "provenance.json");

    // Null means "no positive match yet" - callers should treat this the same as an explicit
    // SelfCreated record (see the class comment): absence IS the default, not an unknown state.
    public FileProvenanceRecord? TryGet(string contentHash)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _records!.GetValueOrDefault(contentHash);
        }
    }

    public void MarkReceived(string contentHash, string detectedChannel)
    {
        lock (_sync)
        {
            EnsureLoaded();
            // First channel to claim a hash wins - see the class comment. A hash already marked
            // Received (by this channel or the other one) is never re-written or downgraded.
            if (_records!.ContainsKey(contentHash)) return;
            _records[contentHash] = new FileProvenanceRecord(contentHash, FileProvenanceOrigins.Received, detectedChannel, DateTimeOffset.UtcNow);
            Save();
        }
    }

    // Content-mutating operations this same service performs on a file AFTER it landed (tile/corner
    // watermarking is the only one today) change its SHA-256, so a hash recorded for the original
    // downloaded/copied bytes stops matching the file's current on-disk content the moment it's
    // watermarked - the very next scan tick that has to recompute the hash from scratch (e.g. after a
    // service restart clears the in-memory _lastSeenWriteTimes fast path) would otherwise resolve this
    // file back to SelfCreated forever, silently defeating file.open-access for every watermarked
    // "Received" file. Confirmed live 2026-09-08: a browser-downloaded, watermark-tagged PDF never
    // encrypted under FileOpenProtection.EnforcementMode="Block" across a service restart because of
    // exactly this. Called by FileInventoryScanner right after it rewrites a file's bytes, carrying the
    // origin forward from the pre-watermark hash to the post-watermark one - a no-op if the old hash
    // was never Received (self-created files are watermarked too) or the new hash is already recorded.
    public void CopyForward(string oldContentHash, string newContentHash)
    {
        if (oldContentHash.Equals(newContentHash, StringComparison.OrdinalIgnoreCase)) return;

        lock (_sync)
        {
            EnsureLoaded();
            if (!_records!.TryGetValue(oldContentHash, out var existing)) return;
            if (_records.ContainsKey(newContentHash)) return;
            _records[newContentHash] = existing with { ContentHash = newContentHash };
            Save();
        }
    }

    private void EnsureLoaded()
    {
        if (_records is not null) return;

        try
        {
            if (File.Exists(MetadataPath))
            {
                var values = JsonSerializer.Deserialize<List<FileProvenanceRecord>>(File.ReadAllText(MetadataPath), JsonDefaults.Options) ?? [];
                _records = values.ToDictionary(item => item.ContentHash, StringComparer.OrdinalIgnoreCase);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the file provenance store; starting empty.");
        }

        _records = new Dictionary<string, FileProvenanceRecord>(StringComparer.OrdinalIgnoreCase);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var temporary = MetadataPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_records!.Values, JsonDefaults.Options));
            File.Move(temporary, MetadataPath, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to persist the file provenance store.");
        }
    }

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
