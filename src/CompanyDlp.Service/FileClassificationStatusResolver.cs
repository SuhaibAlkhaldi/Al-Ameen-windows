using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Backs the getFileClassificationStatus pipe message consumed by CompanyDlp.ShellExtension's
// Explorer hover tooltip and "DLP Classification" column. This is the one place the "don't trust a
// non-Up-to-Date classification" display rule lives, so the .NET Framework shell extension stays a
// dumb formatter with no security logic of its own to drift out of sync. Purely additive/read-only -
// never called by PermissionEvaluator or anything on the enforcement path (the .dlpenc branch below
// reads the same EncryptedFileHashStore/FileClassificationCache data PermissionEvaluator's own
// decrypt-time gate in FileProtectionCoordinator uses, but only ever to *display* a value - it never
// makes an allow/deny decision itself).
public sealed class FileClassificationStatusResolver(
    FileClassificationStatusStore statusStore,
    FileClassificationCache cache,
    FileInventoryScanner scanner,
    FileProtectionEngine engine,
    EncryptedFileHashStore encryptedFileHashStore)
{
    public async Task<FileClassificationStatusResponse> ResolveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        // A .dlpenc file's body is opaque ciphertext, so the ordinary path below (which is keyed off
        // the plaintext file's own content hash, written by FileInventoryScanner while the file was
        // still readable) can never find anything for it - confirmed live 2026-08-25, this always fell
        // through to "Unclassified"/NotScanned for every encrypted file, which is exactly backwards
        // for the one case where seeing the classification *before* acting on the file matters most
        // (deciding whether you're even allowed to decrypt it). Resolve it the same way
        // FileProtectionCoordinator's decrypt-time permission gate does instead: peek the fileId from
        // the unencrypted header (no key unwrap, no decryption of any chunk), then look up the
        // original plaintext's hash/classification from local records.
        if (filePath.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveEncryptedAsync(filePath, cancellationToken);
        }

        var normalized = FileClassificationStatusStore.NormalizePath(filePath);

        if (scanner.IsScanning(normalized))
        {
            return new FileClassificationStatusResponse
            {
                FilePath = filePath,
                Status = FileClassificationStatuses.Scanning,
                Classification = "Unclassified",
                LastScannedAtUtc = statusStore.TryGet(normalized)?.LastScannedAtUtc
            };
        }

        var entry = statusStore.TryGet(normalized);
        if (entry is null)
        {
            return new FileClassificationStatusResponse
            {
                FilePath = filePath,
                Status = FileClassificationStatuses.NotScanned,
                Classification = "Unclassified",
                LastScannedAtUtc = null
            };
        }

        // Only an Up-to-Date status has a classification worth showing - every other status means
        // the cached value (if any) is stale, unresolved, or was never a genuine tier in the first
        // place, so the tooltip always falls back to "Unclassified" rather than risk displaying a
        // classification the file may no longer deserve.
        var classification = entry.Status == FileClassificationStatuses.UpToDate
            ? cache.TryGet(entry.LastClassifiedHash ?? "")?.Classification ?? "Unclassified"
            : "Unclassified";

        return new FileClassificationStatusResponse
        {
            FilePath = filePath,
            Status = entry.Status,
            Classification = classification,
            LastScannedAtUtc = entry.LastScannedAtUtc
        };
    }

    private async Task<FileClassificationStatusResponse> ResolveEncryptedAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            var fileId = await engine.PeekFileIdAsync(filePath, cancellationToken);
            var localEntry = encryptedFileHashStore.TryGet(fileId);

            // Deliberately NOT using EncryptedFileHashStore.UnresolvedClassificationSentinel here like
            // the enforcement gate does - that sentinel exists purely to make PermissionEvaluator's
            // cache-miss convention fail-closed (treat the unknown as VerySecret) for a *decision*.
            // This method only ever displays a value; showing "Very Secret" for a file we genuinely
            // have no record of would misrepresent an unresolved lookup as a real classification the
            // AI actually assigned. Decrypt safety does not depend on what this returns either way -
            // FileProtectionCoordinator resolves and gates independently, every time, regardless of
            // what was ever shown here.
            if (localEntry is null)
            {
                return new FileClassificationStatusResponse
                {
                    FilePath = filePath,
                    Status = FileClassificationStatuses.NotScanned,
                    Classification = "Unclassified",
                    LastScannedAtUtc = null
                };
            }

            var classification = cache.TryGet(localEntry.FileHash)?.Classification ?? "Unclassified";
            return new FileClassificationStatusResponse
            {
                FilePath = filePath,
                Status = FileClassificationStatuses.UpToDate,
                Classification = classification,
                LastScannedAtUtc = localEntry.EncryptedAtUtc
            };
        }
        catch
        {
            // Not a real .dlpenc file (wrong header, truncated, corrupted, etc.) - degrade to
            // "Unclassified" rather than let a formatting/display path throw across the pipe.
            return new FileClassificationStatusResponse
            {
                FilePath = filePath,
                Status = FileClassificationStatuses.NotScanned,
                Classification = "Unclassified",
                LastScannedAtUtc = null
            };
        }
    }
}
