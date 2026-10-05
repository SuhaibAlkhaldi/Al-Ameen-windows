using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Fallback safety net for the File Inventory Report's sync design (see FileInventorySyncPolicy's
// comment): a full walk + diff against FileInventoryLocalStore's believed state, run periodically by
// FileInventorySyncWorker (ReconciliationScanIntervalMinutes) rather than on every tick - this is what
// catches drift FileInventoryChangeWatcher's live FileSystemWatcher could have missed (the agent was
// offline, a watcher failed to start, an event coalesced/was dropped by the OS under heavy I/O, ...).
// Never the primary sync path.
public sealed class FileInventoryReconciliationRunner(
    FileInventoryOutbox outbox,
    SelfWrittenContentRegistry selfWrittenContentRegistry,
    FileInventoryLocalStore localStore,
    FileInventoryContentResolver contentResolver,
    FileProvenanceStore provenanceStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ILogger<FileInventoryReconciliationRunner> logger)
{
    public async Task RunAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var interactiveProfilePath = WatchedFolderPathResolver.ResolveInteractiveUserProfilePath(context.UserSid, logger);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var folder in policy.FileClassification.WatchedFolders)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var expanded = WatchedFolderPathResolver.ExpandWatchedFolderPath(folder, interactiveProfilePath);
                if (!Directory.Exists(expanded)) continue;

                foreach (var path in WatchedFolderEnumerator.EnumerateFilesSafely(expanded, logger))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!FileInventoryContentResolver.IsTrackable(path)) continue;

                    seenPaths.Add(FileInventoryLocalStore.NormalizePath(path));
                    await ReconcileFileAsync(path, cancellationToken);
                }
            }

            // Anything the local store still believes exists but wasn't seen on this walk is gone -
            // this is the one class of change FileInventoryChangeWatcher can never observe directly
            // for changes that happened while the agent wasn't running.
            foreach (var (normalizedPath, entry) in localStore.GetAll())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seenPaths.Contains(normalizedPath)) continue;

                localStore.Remove(entry.FilePath);
                await outbox.EnqueueAsync(new FileInventoryChangeEnvelope
                {
                    ChangeId = Guid.NewGuid(),
                    ChangeType = FileInventoryChangeTypes.Deleted,
                    FilePath = entry.FilePath,
                    OccurredAtUtc = DateTimeOffset.UtcNow
                }, cancellationToken);
            }
        }
        finally
        {
            localStore.Flush();
        }
    }

    private async Task ReconcileFileAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var content = await contentResolver.ResolveAsync(path, cancellationToken);
            if (content is null) return;

            var info = new FileInfo(path);
            var provenance = provenanceStore.TryGet(content.FileHash)?.DetectedChannel ?? FileProvenanceOrigins.SelfCreated;
            var isProtected = path.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase);

            var existing = localStore.TryGet(path);
            // Unchanged since the last time this path was synced (by either the watcher or a previous
            // reconciliation pass) - nothing to report. Compared on the fields that actually change
            // what the backend's row looks like, not on write-time (the fast created/modified path is
            // FileInventoryChangeWatcher's job; this only needs to know whether the reported state is
            // still accurate).
            if (existing is not null
                && string.Equals(existing.FileHash, content.FileHash, StringComparison.OrdinalIgnoreCase)
                && existing.SizeBytes == info.Length
                && existing.ClassificationTier == content.ClassificationTier
                && existing.Provenance == provenance
                && existing.IsProtected == isProtected)
            {
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            localStore.Set(new FileInventoryLocalEntry(
                path, content.FileHash, info.Length, content.ClassificationTier, provenance, isProtected, nowUtc, nowUtc));

            var normalizedText = ContentFingerprinter.TryExtractNormalizedText(path);

            await outbox.EnqueueAsync(new FileInventoryChangeEnvelope
            {
                ChangeId = Guid.NewGuid(),
                ChangeType = existing is null ? FileInventoryChangeTypes.Created : FileInventoryChangeTypes.Modified,
                FilePath = path,
                FileHash = content.FileHash,
                Extension = content.Extension,
                SizeBytes = info.Length,
                ClassificationTier = content.ClassificationTier,
                Provenance = provenance,
                IsProtected = isProtected,
                IsSystemRewrite = selfWrittenContentRegistry.IsSelfWritten(content.FileHash) ? true : null,
                ContentFingerprint = normalizedText is null ? null : ContentFingerprinter.HashNormalizedText(normalizedText),
                ExtractedText = FileVersionTextCapture.ForEnvelope(normalizedText, content.ClassificationTier, isProtected),
                OccurredAtUtc = nowUtc
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not reconcile {Path} during the file inventory reconciliation scan.", path);
        }
    }
}
