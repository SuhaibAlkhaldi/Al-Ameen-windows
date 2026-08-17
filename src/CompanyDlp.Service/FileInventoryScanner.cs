using System.Collections.Concurrent;
using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Proactively classifies files in the watched folders on a poll (DlpWorker calls TickAsync on
// FileClassification.ScanIntervalSeconds), so that PermissionEvaluator's enforcement-time check
// is always a fast local FileClassificationCache lookup instead of a live AI call. Files with no
// cache entry yet are treated as ClassificationTiers.VerySecret by PermissionEvaluator - this
// class only ever narrows that down once a real classification is available.
//
// This class also maintains FileClassificationStatusStore, a path-indexed, display-only status
// (Not Scanned/Pending/Scanning/Up to Date/Reclassification Required/Failed/Unsupported) consumed
// by the Explorer hover-tooltip feature (CompanyDlp.ShellExtension). That store is purely additive:
// PermissionEvaluator never reads it and continues to enforce exclusively off FileClassificationCache.
public sealed class FileInventoryScanner(
    FileClassificationService classificationService,
    FileClassificationCache cache,
    FileClassificationStatusStore statusStore,
    DictionaryRuleStore dictionaryRuleStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ILogger<FileInventoryScanner> logger)
{
    // Per-path last-seen write time, kept in memory only - avoids re-hashing and re-classifying
    // every file in the watched folders on every tick. A service restart re-scans everything once
    // (cheap: cache.TryGet short-circuits any file already classified by hash), which is simpler
    // and more robust than persisting a separate "files seen" index.
    private readonly Dictionary<string, DateTimeOffset> _lastSeenWriteTimes = new(StringComparer.OrdinalIgnoreCase);

    // In-memory only marker for the "Scanning" status - a classify request currently in flight for
    // this path. Never persisted: if the service restarts mid-classification, there is no in-flight
    // request to report on restart, which is correct (the next tick starts fresh).
    private readonly ConcurrentDictionary<string, byte> _scanningNow = new(StringComparer.OrdinalIgnoreCase);

    // Reason codes that mean "we didn't get a real classification" (provider unavailable, no AI
    // provider configured yet, etc.) - a cache entry carrying one of these is a placeholder, not a
    // genuine answer, and should never permanently block a file from being classified for real once
    // the underlying problem (e.g. provider misconfiguration) is fixed. Unchanged from before this
    // feature - governs only whether an already-cached hash gets re-attempted, not the display status.
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

    public bool IsScanning(string path) => _scanningNow.ContainsKey(FileClassificationStatusStore.NormalizePath(path));

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

        var normalized = FileClassificationStatusStore.NormalizePath(path);

        if (_lastSeenWriteTimes.TryGetValue(path, out var known) && known == info.LastWriteTimeUtc) return;

        // Based on the status store, not _lastSeenWriteTimes - a path that failed classification
        // last tick has no _lastSeenWriteTimes entry (retried every tick, by design) but is NOT a
        // new discovery, and must keep showing "Failed" rather than flashing back to "Pending" on
        // every retry attempt.
        var existingStatus = statusStore.TryGet(normalized);
        if (existingStatus is null)
        {
            // A path never seen before this process's lifetime - mark it queued immediately so a
            // hover landing between this line and the classify attempt finishing sees "Pending"
            // rather than nothing at all.
            statusStore.Set(new FileClassificationStatusEntry(
                normalized, FileClassificationStatuses.Pending, null, null, null, DateTimeOffset.UtcNow));
        }
        else if (existingStatus.LastClassifiedHash is not null)
        {
            // Content changed since the last classification we know about - flip the status BEFORE
            // re-hashing, not after. If hashing itself then fails (file locked/mid-write, see below),
            // a stale "Up to Date" would otherwise be left showing indefinitely.
            statusStore.Set(existingStatus with { Status = FileClassificationStatuses.ReclassificationRequired, UpdatedAtUtc = DateTimeOffset.UtcNow });
        }

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
        // A cache entry from an older dictionary-rules version is stale the same way: this exact
        // content may classify differently under the admin's current rules, so it must not be reused
        // as-is either - otherwise editing the Rules page would never affect already-seen content.
        var currentRulesVersion = dictionaryRuleStore.Get().Version;
        var cached = cache.TryGet(hash);
        if (cached is not null && !ProvisionalReasonCodes.Contains(cached.ReasonCode) && cached.RulesVersion == currentRulesVersion)
        {
            var scannedAtUtc = DateTimeOffset.UtcNow;
            var (taggedPath, taggedNormalized) = ApplyFilenameTag(path, normalized, cached.Classification, policy);
            var effectiveWriteTimeUtc = ApplyContentWatermarkIfEnabled(taggedPath, cached.Classification, scannedAtUtc, policy, info.LastWriteTimeUtc);
            _lastSeenWriteTimes.Remove(path);
            _lastSeenWriteTimes[taggedPath] = effectiveWriteTimeUtc;
            if (taggedNormalized != normalized) statusStore.Delete(normalized);
            // LastScannedAtUtc means "when did the scanner last look at THIS file", not "when was
            // this content first classified" - cache.TryGet can return a hit from a completely
            // different, older file that happened to have identical content, so cached.ClassifiedAtUtc
            // would show a stale/misleading timestamp here.
            statusStore.Set(new FileClassificationStatusEntry(
                taggedNormalized, FileClassificationStatuses.UpToDate, hash, scannedAtUtc, cached.ReasonCode, DateTimeOffset.UtcNow));
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

        _scanningNow[normalized] = 0;
        try
        {
            // A second, fresh read of the file's bytes for the real AI classification call - simpler
            // and more robust than seeking the hashing stream back to 0 (the file could theoretically
            // be mid-write between the two reads, but SHA256 already captured a real snapshot; a
            // content mismatch here at worst yields a stale-by-one-scan classification, corrected on
            // the next tick once the file settles). Deliberately scoped tightly (not `await using var`
            // at the method level) and disposed immediately after use - ApplyFilenameTag/
            // ApplyContentWatermarkIfEnabled below need to rename/rewrite this exact file, and Windows
            // refuses File.Move on a path that still has an open handle without delete-share rights
            // (confirmed live: UnauthorizedAccessException from MoveFile when this stream was still
            // open across the rename attempt).
            FileClassificationResult result;
            await using (var contentStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                result = await classificationService.ClassifyAsync(request, context, cancellationToken, contentStream);
            }
            cache.Set(new CachedFileClassification(hash, result.Classification, result.ReasonCode, DateTimeOffset.UtcNow, currentRulesVersion));

            if (FileClassificationReasonCodes.UnsupportedReasonCodes.Contains(result.ReasonCode))
            {
                // The AI rejects this file type outright (e.g. a blocked extension) - not transient,
                // retrying won't change the outcome, so mark it seen like a genuine result to avoid
                // resubmitting the same rejected file on every tick.
                _lastSeenWriteTimes[path] = info.LastWriteTimeUtc;
                statusStore.Set(new FileClassificationStatusEntry(
                    normalized, FileClassificationStatuses.Unsupported,
                    existingStatus?.LastClassifiedHash, existingStatus?.LastScannedAtUtc, result.ReasonCode, DateTimeOffset.UtcNow));
            }
            else if (FileClassificationReasonCodes.TransientFailureReasonCodes.Contains(result.ReasonCode))
            {
                // Deliberately NOT marking _lastSeenWriteTimes here - a transient failure (network
                // blip, momentary AI-API hiccup) should be retried on the very next tick, not only
                // after a service restart.
                statusStore.Set(new FileClassificationStatusEntry(
                    normalized, FileClassificationStatuses.Failed,
                    existingStatus?.LastClassifiedHash, existingStatus?.LastScannedAtUtc, result.ReasonCode, DateTimeOffset.UtcNow));
            }
            else
            {
                var scannedAtUtc = DateTimeOffset.UtcNow;
                var (taggedPath, taggedNormalized) = ApplyFilenameTag(path, normalized, result.Classification, policy);
                var effectiveWriteTimeUtc = ApplyContentWatermarkIfEnabled(taggedPath, result.Classification, scannedAtUtc, policy, info.LastWriteTimeUtc);
                _lastSeenWriteTimes.Remove(path);
                _lastSeenWriteTimes[taggedPath] = effectiveWriteTimeUtc;
                if (taggedNormalized != normalized) statusStore.Delete(normalized);
                statusStore.Set(new FileClassificationStatusEntry(
                    taggedNormalized, FileClassificationStatuses.UpToDate, hash, scannedAtUtc, result.ReasonCode, DateTimeOffset.UtcNow));
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Background classification failed for {Path}.", path);
            statusStore.Set(new FileClassificationStatusEntry(
                normalized, FileClassificationStatuses.Failed,
                existingStatus?.LastClassifiedHash, existingStatus?.LastScannedAtUtc, "UnhandledClassificationException", DateTimeOffset.UtcNow));
        }
        finally
        {
            _scanningNow.TryRemove(normalized, out _);
        }
    }

    // Renames the file to reflect its classification tier (see FilenameClassificationTagger) when
    // the policy opts in. Returns the path/normalized-path callers should use from this point on -
    // unchanged from the input if tagging is disabled, already correct, or the rename didn't happen
    // (locked file, name collision) so the caller falls back to keeping the file under its old name
    // and simply retries next tick, exactly like any other soft failure in this class.
    private (string Path, string Normalized) ApplyFilenameTag(string path, string normalized, string classification, FileClassificationPolicy policy)
    {
        if (!policy.FilenameTaggingEnabled) return (path, normalized);

        var directory = Path.GetDirectoryName(path);
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName)) return (path, normalized);

        var desiredFileName = FilenameClassificationTagger.BuildTaggedFileName(fileName, classification);
        if (string.Equals(desiredFileName, fileName, StringComparison.Ordinal)) return (path, normalized);

        var desiredPath = Path.Combine(directory, desiredFileName);
        if (File.Exists(desiredPath))
        {
            logger.LogDebug("Skipped filename tagging for {Path}; a file already exists at {DesiredPath}.", path, desiredPath);
            return (path, normalized);
        }

        try
        {
            File.Move(path, desiredPath);
            return (desiredPath, FileClassificationStatusStore.NormalizePath(desiredPath));
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not rename {Path} to reflect its classification; will retry next scan.", path);
            return (path, normalized);
        }
    }

    // Stamps the classification into the file's own content (see ContentWatermarker) when the
    // policy opts in - a separate, independently-gated step from ApplyFilenameTag, called on
    // whatever path ApplyFilenameTag already settled on. Returns the write-time callers should
    // record in _lastSeenWriteTimes: watermarking rewrites the file's bytes, which bumps its real
    // LastWriteTimeUtc, so that must be re-read from disk after a successful watermark - reusing
    // the pre-watermark timestamp here would make the very next tick see what looks like a fresh
    // edit (our own write) and loop forever: reclassify -> re-watermark -> reclassify.
    private DateTime ApplyContentWatermarkIfEnabled(string path, string classification, DateTimeOffset scannedAtUtc, FileClassificationPolicy policy, DateTime fallbackWriteTimeUtc)
    {
        if (!policy.ContentWatermarkingEnabled) return fallbackWriteTimeUtc;
        if (!ContentWatermarker.ApplyWatermark(path, classification, scannedAtUtc, logger)) return fallbackWriteTimeUtc;

        try
        {
            return new FileInfo(path).LastWriteTimeUtc;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Watermarked {Path} but could not re-read its write time; using the pre-watermark timestamp.", path);
            return fallbackWriteTimeUtc;
        }
    }

    // node_modules folders hold thousands of library files that are never user content and don't
    // need classification - scanning them anyway means the very first tick after a fresh npm
    // install can spend hours making real AI classification calls for every one of those files
    // before it ever gets back around to re-checking an actual user document.
    private static bool IsInsideNodeModules(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase));
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

                if (!IsInsideNodeModules(current)) yield return current;
            }
        }
    }
}
