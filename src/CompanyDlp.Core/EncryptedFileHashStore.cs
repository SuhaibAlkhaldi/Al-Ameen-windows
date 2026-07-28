using System.Text.Json;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Core;

public sealed record EncryptedFileHashEntry(Guid FileId, string FileHash, DateTimeOffset EncryptedAtUtc);

// Local, persisted fileId -> original-plaintext-hash lookup, written by FileProtectionCoordinator right
// after a successful file.encrypt (the plaintext and its SHA-256 are only ever available before the
// plaintext is deleted - see FileProtectionEngine.EncryptAndDeleteOriginalAsync). The .dlpenc header
// already carries fileId in the clear (no format change needed, see FileProtectionEngine.PeekFileIdAsync),
// so a decrypt attempt can read the header cheaply, resolve the hash from here, and hand it to
// PermissionEvaluator exactly like a browser-upload check does - reusing FileClassificationCache (keyed
// by hash) as the single source of truth for the file's classification tier, rather than duplicating a
// second tier value here that could drift out of sync if the file is ever reclassified.
public sealed class EncryptedFileHashStore(PolicyStore policyStore, ILogger<EncryptedFileHashStore> logger)
{
    private readonly object _sync = new();
    private Dictionary<Guid, EncryptedFileHashEntry>? _entries;

    public EncryptedFileHashEntry? TryGet(Guid fileId)
    {
        lock (_sync)
        {
            EnsureLoaded();
            return _entries!.GetValueOrDefault(fileId);
        }
    }

    public void Set(EncryptedFileHashEntry entry)
    {
        lock (_sync)
        {
            EnsureLoaded();
            _entries![entry.FileId] = entry;
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
                var values = JsonSerializer.Deserialize<List<EncryptedFileHashEntry>>(File.ReadAllText(path), JsonDefaults.Options) ?? [];
                _entries = values.ToDictionary(item => item.FileId);
                return;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unable to load the encrypted-file hash store; starting empty.");
        }

        _entries = new Dictionary<Guid, EncryptedFileHashEntry>();
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
            logger.LogError(exception, "Unable to persist the encrypted-file hash store.");
        }
    }

    // A .dlpenc file whose original hash genuinely cannot be resolved (no local record, and the
    // backend's historical audit trail - see BackendApiClient.TryGetHistoricalFileHashAsync - has no
    // recoverable entry either, e.g. a fully-offline-encrypted or pre-audit-logging legacy file) must
    // never be evaluated as if it had no classification context at all - that would fall back to the
    // action's plain default-allow (see PermissionEvaluator), letting an unclassified file bypass
    // tier-gated decrypt entirely. This sentinel is deterministic per fileId, guaranteed to miss
    // FileClassificationCache (never collides with a real 64-hex-char SHA-256), so PermissionEvaluator's
    // existing cache-miss convention (fail-closed to ClassificationTiers.VerySecret) applies exactly as
    // it would for any other unresolvable file - no new evaluator logic needed.
    public static string UnresolvedClassificationSentinel(Guid fileId) => $"unresolved-classification:{fileId:N}";

    private string GetStorePath() => Path.Combine(GetRoot(), "encrypted-file-hashes.json");

    private string GetRoot()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp");
    }
}
