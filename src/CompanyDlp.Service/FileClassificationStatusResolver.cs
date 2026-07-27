using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Backs the getFileClassificationStatus pipe message consumed by CompanyDlp.ShellExtension's
// Explorer hover tooltip. This is the one place the "don't trust a non-Up-to-Date classification"
// display rule lives, so the .NET Framework shell extension stays a dumb formatter with no security
// logic of its own to drift out of sync. Purely additive/read-only - never called by
// PermissionEvaluator or anything on the enforcement path.
public sealed class FileClassificationStatusResolver(
    FileClassificationStatusStore statusStore,
    FileClassificationCache cache,
    FileInventoryScanner scanner)
{
    public FileClassificationStatusResponse Resolve(string filePath)
    {
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
}
