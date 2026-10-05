using System.Collections.Concurrent;

namespace CompanyDlp.Service;

// Remembers the content hashes this agent itself produced when it rewrote a file (the classification
// watermark FileInventoryScanner stamps into the file's bytes). A later inventory change whose content
// hash matches one of these is a system rewrite, not a user edit, so the backend can keep it out of the
// file's modification history. Keyed by content hash, not path: a classification tag rename changes the
// path right after the rewrite, but the bytes - and therefore the hash - stay the same. Entries are
// bounded: the file watcher reports a self-write within moments, but the reconciliation pass (hourly by
// default) can report it much later, so entries live long enough to cover one reconciliation interval.
public sealed class SelfWrittenContentRegistry
{
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _writtenHashes = new(StringComparer.Ordinal);

    public void RecordRewrite(string path)
    {
        var hash = FileInventoryContentResolver.ComputeHash(path);
        if (hash is null) return;

        var nowUtc = DateTimeOffset.UtcNow;
        _writtenHashes[hash] = nowUtc;
        Prune(nowUtc);
    }

    public bool IsSelfWritten(string contentHash)
    {
        if (!_writtenHashes.TryGetValue(contentHash, out var writtenAtUtc)) return false;

        if (DateTimeOffset.UtcNow - writtenAtUtc > EntryLifetime)
        {
            _writtenHashes.TryRemove(contentHash, out _);
            return false;
        }

        return true;
    }

    private void Prune(DateTimeOffset nowUtc)
    {
        foreach (var (hash, writtenAtUtc) in _writtenHashes)
        {
            if (nowUtc - writtenAtUtc > EntryLifetime)
            {
                _writtenHashes.TryRemove(hash, out _);
            }
        }
    }
}
