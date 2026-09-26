using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// One-shot logic (not its own BackgroundService - invoked by FileInventorySyncWorker's own tick loop
// exactly once, the first time FileInventoryLocalStore.InitialSyncCompleted is false) that walks
// WatchedFolders the same way FileInventoryScanner does and enqueues one Created change per already-
// classified, trackable file onto FileInventoryOutbox. See FileInventoryChangeWatcher's class comment
// for why an unclassified file is silently skipped rather than reported with a fabricated tier, and
// why "trackable" includes .dlpenc (an already-encrypted file present at first sync, not just one
// encrypted later).
public sealed class FileInventoryInitialSyncRunner(
    FileInventoryOutbox outbox,
    FileInventoryLocalStore localStore,
    FileInventoryContentResolver contentResolver,
    FileProvenanceStore provenanceStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    ILogger<FileInventoryInitialSyncRunner> logger)
{
    public async Task RunAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var interactiveProfilePath = WatchedFolderPathResolver.ResolveInteractiveUserProfilePath(context.UserSid, logger);

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
                    await TryEnqueueAsync(path, cancellationToken);
                }
            }
        }
        finally
        {
            localStore.Flush();
        }
    }

    private async Task TryEnqueueAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!FileInventoryContentResolver.IsTrackable(path)) return;

            var content = await contentResolver.ResolveAsync(path, cancellationToken);
            if (content is null) return; // not yet classified - a later reconciliation pass will catch it

            var info = new FileInfo(path);
            var provenance = provenanceStore.TryGet(content.FileHash)?.DetectedChannel ?? FileProvenanceOrigins.SelfCreated;
            var isProtected = path.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase);
            var nowUtc = DateTimeOffset.UtcNow;

            localStore.Set(new FileInventoryLocalEntry(
                path, content.FileHash, info.Length, content.ClassificationTier, provenance, isProtected, nowUtc, nowUtc));

            await outbox.EnqueueAsync(new FileInventoryChangeEnvelope
            {
                ChangeId = Guid.NewGuid(),
                ChangeType = FileInventoryChangeTypes.Created,
                FilePath = path,
                FileHash = content.FileHash,
                Extension = content.Extension,
                SizeBytes = info.Length,
                ClassificationTier = content.ClassificationTier,
                Provenance = provenance,
                IsProtected = isProtected,
                OccurredAtUtc = nowUtc
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not enqueue {Path} during the file inventory initial sync.", path);
        }
    }
}
