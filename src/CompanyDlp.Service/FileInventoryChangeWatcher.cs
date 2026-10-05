using System.Collections.Concurrent;
using System.Linq;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Detects file created/modified/renamed/deleted events in the watched folders and enqueues the
// corresponding change onto FileInventoryOutbox for FileInventorySyncWorker to send to the backend -
// the "incremental" half of the File Inventory Report's sync design (see FileInventorySyncPolicy's
// comment for the other two halves, initial sync and reconciliation).
//
// Same dedicated-BackgroundService shape as DesktopAppProvenanceMonitor: FileSystemWatcher callbacks
// do no I/O, only enqueue the raw event args and signal - all real work (stabilization wait, hashing,
// classification/provenance lookup, outbox write) happens on ExecuteAsync's own async context, never
// on a callback thread.
//
// Deliberately a NEW dedicated watcher rather than extending FileInventoryScanner: that scanner has no
// delete/rename detection today (a poll-based full walk can only ever infer "missing" by diffing two
// full snapshots - that's exactly what FileInventoryReconciliationRunner is for, not a fast incremental
// path), and keeping this independent of FileClassification.ScanIntervalSeconds' own cadence avoids
// coupling inventory-sync latency to classification-scan latency, which have different tuning needs.
//
// This only ever REPORTS a classification FileInventoryScanner already computed and cached - it never
// classifies anything itself. A Created/Modified event for content with no FileClassificationCache
// entry yet (not yet scanned, unsupported extension, or over MaximumFileSizeBytes) is simply skipped
// rather than reported with a fabricated tier; FileInventoryReconciliationRunner's periodic pass picks
// it up once (if ever) a real classification exists. Same reasoning narrows the File Inventory Report's
// practical scope to the same "classifiable document" universe FileInventoryScanner already limits
// itself to, PLUS .dlpenc (see FileInventoryContentResolver.IsTrackable) - reporting on files this
// system has no classification opinion about at all (images, executables, archives, ...) wouldn't
// serve the report's purpose, but an encrypted file must stay trackable or it vanishes from the
// report at the exact moment its Protection status becomes the most interesting thing about it.
public sealed class FileInventoryChangeWatcher(
    PolicyStore policyStore,
    FileInventoryOutbox outbox,
    FileInventoryLocalStore localStore,
    FileInventoryContentResolver contentResolver,
    FileProvenanceStore provenanceStore,
    InteractiveUserContextProvider interactiveUserContextProvider,
    SelfWrittenContentRegistry selfWrittenContentRegistry,
    ILogger<FileInventoryChangeWatcher> logger) : BackgroundService
{
    private readonly ConcurrentQueue<FileSystemEventArgs> _pendingEvents = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _startAttempted;

    // Found live 2026-09-27, tracing a duplicate-report bug through to the backend and back: Windows'
    // FileSystemWatcher is documented to sometimes raise a spurious extra Changed notification for the
    // SAME final path immediately alongside a genuine Renamed notification for a single underlying
    // File.Move (e.g. FileInventoryScanner.ApplyFilenameTag renaming "[Secret] x.txt" -> "[Public]
    // x.txt") - not a bug in this class's own logic, a well-known quirk of the OS-level watcher itself.
    // HandleCreatedOrModifiedAsync has no way to tell that echo apart from a genuine, separate content
    // edit that merely happens to land on the same path a moment later - both look identical by the
    // time they reach this queue. Since HandleRenamedAsync already reports a Renamed change with full
    // lineage (old hash/tier) for that same path, a Changed/Created echo immediately after it is pure
    // noise: at best a no-op once it reaches the backend (already covered by
    // AgentFileInventoryService's idempotent upsert), at worst it strips the real transition's lineage
    // (a blind, hash-less "Genesis" row masking a real classification change) if it happens to be
    // treated as arriving first. Suppressing it here at the source is cleaner than asking the backend
    // to keep guessing after the fact. Deliberately a short window (few seconds) and keyed on the exact
    // exact target path only - a genuine, unrelated edit to the same path minutes later is never
    // affected, and even a suppressed echo in the rare coincidental-timing case is self-healed by
    // FileInventoryReconciliationRunner's periodic full re-sync, so nothing is permanently lost.
    private static readonly TimeSpan RenameEchoSuppressionWindow = TimeSpan.FromSeconds(3);
    private readonly Dictionary<string, DateTimeOffset> _recentlyRenamedTargetPaths = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            if (!policy.Enabled || !policy.FileInventorySync.Enabled || !policy.FileClassification.Enabled)
            {
                Stop();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                continue;
            }

            EnsureStarted(policy.FileClassification.WatchedFolders);

            try
            {
                // Same 2-second-timeout-just-to-recheck-policy-flips idiom as DesktopAppProvenanceMonitor -
                // the semaphore drives fast reaction to a real detection.
                await _signal.WaitAsync(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            while (_pendingEvents.TryDequeue(out var fileEvent))
            {
                await HandleEventAsync(fileEvent, stoppingToken);
            }
        }

        Stop();
    }

    private void EnsureStarted(IReadOnlyList<string> watchedFolders)
    {
        if (_startAttempted) return;
        _startAttempted = true;
        StartFileWatchers(watchedFolders);
    }

    private void StartFileWatchers(IReadOnlyList<string> watchedFolders)
    {
        var context = interactiveUserContextProvider.GetActiveConsoleUser();
        var interactiveProfilePath = WatchedFolderPathResolver.ResolveInteractiveUserProfilePath(context.UserSid, logger);

        foreach (var folder in watchedFolders)
        {
            var expanded = WatchedFolderPathResolver.ExpandWatchedFolderPath(folder, interactiveProfilePath);
            if (!Directory.Exists(expanded)) continue;

            try
            {
                var watcher = new FileSystemWatcher(expanded)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                };
                watcher.Created += OnEvent;
                watcher.Changed += OnEvent;
                watcher.Renamed += OnEvent;
                watcher.Deleted += OnEvent;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not watch folder {Folder} for file inventory changes.", expanded);
            }
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        _pendingEvents.Enqueue(e);
        _signal.Release();
    }

    private async Task HandleEventAsync(FileSystemEventArgs fileEvent, CancellationToken cancellationToken)
    {
        try
        {
            if (WatchedFolderEnumerator.IsInsideExcludedDirectory(fileEvent.FullPath)) return;

            if (fileEvent is RenamedEventArgs renamed)
            {
                await HandleRenamedAsync(renamed, cancellationToken);
                return;
            }

            switch (fileEvent.ChangeType)
            {
                case WatcherChangeTypes.Deleted:
                    HandleDeleted(fileEvent.FullPath);
                    break;
                case WatcherChangeTypes.Created:
                case WatcherChangeTypes.Changed:
                    await HandleCreatedOrModifiedAsync(fileEvent.FullPath, cancellationToken);
                    break;
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not process a file inventory change event for {Path}.", fileEvent.FullPath);
        }
    }

    private async Task HandleCreatedOrModifiedAsync(string path, CancellationToken cancellationToken)
    {
        if (!FileInventoryContentResolver.IsTrackable(path)) return;
        if (IsLikelyRenameEcho(path)) return; // see this class's RenameEchoSuppressionWindow comment
        if (!WaitUntilStable(path)) return; // still being written, or already gone - skip, nothing lost

        var change = await BuildChangeForExistingFileAsync(FileInventoryChangeTypes.Created, path, oldFilePath: null, cancellationToken);
        if (change is null) return; // not yet classified - see class comment

        await outbox.EnqueueAsync(change, cancellationToken);
    }

    private void HandleDeleted(string path)
    {
        // No stabilization wait, no hashing - the file is already gone, there's nothing left to read.
        // Extension-filtered the same as Created/Modified: a path we'd never have reported as Created
        // (untrackable extension) was never part of the synced universe, so its deletion is a no-op.
        if (!FileInventoryContentResolver.IsTrackable(path)) return;

        localStore.Remove(path);
        _ = outbox.EnqueueAsync(new FileInventoryChangeEnvelope
        {
            ChangeId = Guid.NewGuid(),
            ChangeType = FileInventoryChangeTypes.Deleted,
            FilePath = path,
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
    }

    private async Task HandleRenamedAsync(RenamedEventArgs renamed, CancellationToken cancellationToken)
    {
        // .dlpenc counts as trackable here (see IsTrackable) specifically so an auto-encryption
        // rename (report.docx -> report.docx.dlpenc) lands in the "ordinary rename" branch below,
        // not the "left the trackable universe" one - an encrypted file must stay visible in the
        // report with Protection=true, not disappear the moment it's protected.
        var oldTrackable = FileInventoryContentResolver.IsTrackable(renamed.OldFullPath);
        var newTrackable = FileInventoryContentResolver.IsTrackable(renamed.FullPath);

        if (!oldTrackable && !newTrackable) return; // never tracked before, still not trackable now

        if (oldTrackable && !newTrackable)
        {
            // Left the trackable universe (e.g. report.docx -> report.docx.bak) - report as a deletion
            // of the old path, same as HandleDeleted.
            HandleDeleted(renamed.OldFullPath);
            return;
        }

        if (!WaitUntilStable(renamed.FullPath)) return;

        if (!oldTrackable)
        {
            // Entered the trackable universe (e.g. draft.bak -> draft.docx) - report as a fresh Created
            // at the new path; there is no "old" tracked row to soft-delete.
            var createdChange = await BuildChangeForExistingFileAsync(FileInventoryChangeTypes.Created, renamed.FullPath, oldFilePath: null, cancellationToken);
            if (createdChange is null) return;
            await outbox.EnqueueAsync(createdChange, cancellationToken);
            MarkRecentlyRenamed(renamed.FullPath);
            return;
        }

        // Ordinary rename/move within the trackable universe (this is also the path an auto-encrypt
        // or auto-decrypt rename takes, now that .dlpenc <-> plaintext are both trackable).
        var change = await BuildChangeForExistingFileAsync(FileInventoryChangeTypes.Renamed, renamed.FullPath, renamed.OldFullPath, cancellationToken);
        if (change is null) return;

        localStore.Remove(renamed.OldFullPath);
        await outbox.EnqueueAsync(change, cancellationToken);
        MarkRecentlyRenamed(renamed.FullPath);
    }

    // Shared by the Created/Modified and Renamed paths: resolves the path's current content (hash,
    // classification, real extension - transparently handling a .dlpenc via FileInventoryContentResolver)
    // and looks up provenance, returning null (meaning "skip - nothing to report yet") if this content
    // hasn't been classified by FileInventoryScanner yet - see class comment.
    private async Task<FileInventoryChangeEnvelope?> BuildChangeForExistingFileAsync(string changeType, string path, string? oldFilePath, CancellationToken cancellationToken)
    {
        var content = await contentResolver.ResolveAsync(path, cancellationToken);
        if (content is null) return null;

        long sizeBytes;
        try
        {
            sizeBytes = new FileInfo(path).Length;
        }
        catch
        {
            return null; // gone again between the stability check and here - next event will catch it
        }

        var provenance = provenanceStore.TryGet(content.FileHash)?.DetectedChannel ?? FileProvenanceOrigins.SelfCreated;
        var isProtected = path.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase);
        var nowUtc = DateTimeOffset.UtcNow;

        localStore.Set(new FileInventoryLocalEntry(
            path, content.FileHash, sizeBytes, content.ClassificationTier, provenance, isProtected, nowUtc, nowUtc));

        return new FileInventoryChangeEnvelope
        {
            ChangeId = Guid.NewGuid(),
            ChangeType = changeType,
            FilePath = path,
            OldFilePath = oldFilePath,
            FileHash = content.FileHash,
            Extension = content.Extension,
            SizeBytes = sizeBytes,
            ClassificationTier = content.ClassificationTier,
            Provenance = provenance,
            IsProtected = isProtected,
            IsSystemRewrite = selfWrittenContentRegistry.IsSelfWritten(content.FileHash) ? true : null,
            ContentFingerprint = ContentFingerprinter.TryCompute(path),
            OccurredAtUtc = nowUtc
        };
    }

    // See RenameEchoSuppressionWindow's comment. Called only from HandleEventAsync's own single
    // sequential processing loop (never from the raw OnEvent callback, which only touches the
    // already-thread-safe _pendingEvents/_signal), so a plain Dictionary is safe here - no concurrent
    // access to guard against.
    private void MarkRecentlyRenamed(string targetPath)
    {
        PruneExpiredRenameMarks();
        _recentlyRenamedTargetPaths[targetPath] = DateTimeOffset.UtcNow;
    }

    private bool IsLikelyRenameEcho(string path)
    {
        PruneExpiredRenameMarks();
        return _recentlyRenamedTargetPaths.ContainsKey(path);
    }

    private void PruneExpiredRenameMarks()
    {
        var cutoffUtc = DateTimeOffset.UtcNow - RenameEchoSuppressionWindow;
        foreach (var expiredPath in _recentlyRenamedTargetPaths
            .Where(entry => entry.Value < cutoffUtc)
            .Select(entry => entry.Key)
            .ToList())
        {
            _recentlyRenamedTargetPaths.Remove(expiredPath);
        }
    }

    // Same short exclusive-open retry loop as DesktopAppProvenanceMonitor.WaitUntilStable - FileSystemWatcher
    // has no native "writer closed the file" event.
    private static bool WaitUntilStable(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
        return false;
    }

    private void Stop()
    {
        if (!_startAttempted) return;

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnEvent;
                watcher.Changed -= OnEvent;
                watcher.Renamed -= OnEvent;
                watcher.Deleted -= OnEvent;
                watcher.Dispose();
            }
            catch { }
        }
        _watchers.Clear();

        _startAttempted = false;
    }
}
