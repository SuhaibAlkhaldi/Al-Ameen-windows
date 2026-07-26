using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Proactively classifies files in the watched folders on a poll (DlpWorker calls TickAsync on
// FileClassification.ScanIntervalSeconds), so that PermissionEvaluator's enforcement-time check
// is always a fast local FileClassificationCache lookup instead of a live AI call. Files with no
// cache entry yet are treated as ClassificationTiers.VerySecret by PermissionEvaluator - this
// class only ever narrows that down once a real classification is available.
public sealed class FileInventoryScanner(
    FileClassificationService classificationService,
    FileClassificationCache cache,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ILogger<FileInventoryScanner> logger)
{
    // Per-path last-seen write time, kept in memory only - avoids re-hashing and re-classifying
    // every file in the watched folders on every tick. A service restart re-scans everything once
    // (cheap: cache.TryGet short-circuits any file already classified by hash), which is simpler
    // and more robust than persisting a separate "files seen" index.
    private readonly Dictionary<string, DateTimeOffset> _lastSeenWriteTimes = new(StringComparer.OrdinalIgnoreCase);

    // Reason codes that mean "we didn't get a real classification" (provider unavailable, no AI
    // provider configured yet, etc.) - a cache entry carrying one of these is a placeholder, not a
    // genuine answer, and should never permanently block a file from being classified for real once
    // the underlying problem (e.g. provider misconfiguration) is fixed.
    private static readonly HashSet<string> ProvisionalReasonCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BlockAllUntilAiProviderAvailable",
        "ClassificationProviderUnavailableFailClosed",
        "NoFileContentAvailableForAiClassification",
        // The backend's own extension-blocklist stub (DLPManagementSystem's FileClassificationService)
        // falls back to these two when its AI API isn't configured/reachable - same "not a real
        // verdict" category as the agent-side codes above.
        "BlockedFileExtension",
        "DefaultAllowStubClassification"
    };

    public async Task TickAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var fileClassification = policy.FileClassification;
        if (!fileClassification.Enabled || !fileClassification.BackgroundScanEnabled) return;

        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var wasBackfillPending = !cache.BackfillCompleted;

        foreach (var folder in fileClassification.WatchedFolders)
        {
            if (cancellationToken.IsCancellationRequested) return;

            var expanded = Environment.ExpandEnvironmentVariables(folder);
            if (!Directory.Exists(expanded)) continue;

            foreach (var path in EnumerateFilesSafely(expanded))
            {
                if (cancellationToken.IsCancellationRequested) return;
                await ClassifyIfNeededAsync(path, fileClassification, context, cancellationToken);
            }
        }

        if (wasBackfillPending) cache.BackfillCompleted = true;
    }

    private async Task ClassifyIfNeededAsync(
        string path,
        FileClassificationPolicy policy,
        ClientContext context,
        CancellationToken cancellationToken)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > policy.MaximumFileSizeBytes) return;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Skipped {Path}; file metadata could not be read.", path);
            return;
        }

        if (_lastSeenWriteTimes.TryGetValue(path, out var known) && known == info.LastWriteTimeUtc) return;

        string hash;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Skipped hashing {Path}; the file could not be read.", path);
            return;
        }

        // A cached result from a provisional path (the BlockAll stub, or the AI provider being
        // unreachable at the time) never counts as a genuine answer - skip it so the file gets a
        // real attempt on this tick instead of being stuck with a stale placeholder forever (e.g.
        // "Sensitive"/BlockAllUntilAiProviderAvailable from before the real provider was configured).
        var cached = cache.TryGet(hash);
        if (cached is not null && !ProvisionalReasonCodes.Contains(cached.ReasonCode))
        {
            _lastSeenWriteTimes[path] = info.LastWriteTimeUtc;
            return;
        }

        var request = new FileClassificationRequest
        {
            FileName = info.Name,
            Extension = info.Extension,
            SizeBytes = info.Length,
            Sha256 = hash,
            Channel = "background-scan",
            Destination = ""
        };

        try
        {
            // A second, fresh read of the file's bytes for the real AI classification call - simpler
            // and more robust than seeking the hashing stream back to 0 (the file could theoretically
            // be mid-write between the two reads, but SHA256 already captured a real snapshot; a
            // content mismatch here at worst yields a stale-by-one-scan classification, corrected on
            // the next tick once the file settles).
            await using var contentStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var result = await classificationService.ClassifyAsync(request, context, cancellationToken, contentStream);
            cache.Set(new CachedFileClassification(hash, result.Classification, result.ReasonCode, DateTimeOffset.UtcNow));
            // Only mark this write-time as "seen" once classification actually succeeded - marking it
            // unconditionally (as this used to do, before the try block even ran) meant a single
            // transient failure (a network blip, a momentary AI-API hiccup) permanently stuck the file
            // as unclassified: the next tick would see the same LastWriteTimeUtc and skip it forever,
            // never retrying, since nothing about the file itself ever changes again.
            _lastSeenWriteTimes[path] = info.LastWriteTimeUtc;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Background classification failed for {Path}.", path);
        }
    }

    private IEnumerable<string> EnumerateFilesSafely(string root)
    {
        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).GetEnumerator();
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Unable to enumerate {Root} for background file classification.", root);
        }

        if (enumerator is null) yield break;

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext()) yield break;
                    current = enumerator.Current;
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "Stopped enumerating {Root} for background file classification.", root);
                    yield break;
                }

                yield return current;
            }
        }
    }
}
