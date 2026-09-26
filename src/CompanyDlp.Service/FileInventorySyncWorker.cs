using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Drains FileInventoryOutbox to the backend (mirrors AuditSyncWorker's own drain-and-send shape), and
// additionally owns the other two halves of the File Inventory Report's sync design: running
// FileInventoryInitialSyncRunner exactly once (when FileInventoryLocalStore.InitialSyncCompleted is
// false) and FileInventoryReconciliationRunner on its own interval. Both runners enqueue onto the same
// outbox FileInventoryChangeWatcher uses; this worker drains and sends whatever's pending on every
// tick regardless of which of the three produced it, tagging the batch's SyncKind by what triggered
// this particular tick's work (informational + drives the Reconciliation delete-leniency on the
// backend - see AgentFileInventoryService's class comment; it is not a strict partition of the outbox
// itself, which is intentionally just one shared FIFO queue).
public sealed class FileInventorySyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    FileInventoryOutbox outbox,
    FileInventoryLocalStore localStore,
    FileInventoryInitialSyncRunner initialSyncRunner,
    FileInventoryReconciliationRunner reconciliationRunner,
    BackendApiClient backendApiClient,
    ILogger<FileInventorySyncWorker> logger) : BackgroundService
{
    private DateTimeOffset? _lastReconciliationAtUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.FileInventorySync.IncrementalSyncSeconds, 2, 3600));

            try
            {
                if (policy.Enabled && policy.FileInventorySync.Enabled && policy.Backend.Enabled)
                {
                    await RunOneTickAsync(policy, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                outbox.RecordSyncError($"{exception.GetType().Name}: {exception.Message}");
                logger.LogWarning(exception, "Company DLP file inventory synchronization failed; changes remain in the encrypted outbox.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task RunOneTickAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        if (!localStore.InitialSyncCompleted)
        {
            await initialSyncRunner.RunAsync(policy, cancellationToken);
            await DrainAndSendAsync(FileInventorySyncKinds.InitialFull, policy, cancellationToken);

            // Only set once the walk-and-enqueue above completed without being cancelled - a service
            // stop mid-walk must not mark this done, or a large chunk of the watched folders would
            // never get a first sync attempt (see FileClassificationCache.BackfillCompleted for the
            // same "only mark done after an uninterrupted full pass" reasoning). Marked done regardless
            // of whether the send above fully succeeded - the outbox itself is what's durable; any
            // items that failed to send just sit in `pending/` and get retried by ordinary Incremental
            // drains on later ticks, same as any other outbox item.
            localStore.InitialSyncCompleted = true;
            _lastReconciliationAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        if (IsReconciliationDue(policy))
        {
            await reconciliationRunner.RunAsync(policy, cancellationToken);
            await DrainAndSendAsync(FileInventorySyncKinds.Reconciliation, policy, cancellationToken);
            _lastReconciliationAtUtc = DateTimeOffset.UtcNow;
            return;
        }

        await DrainAndSendAsync(FileInventorySyncKinds.Incremental, policy, cancellationToken);
    }

    // Null (never reconciled yet this process lifetime) is treated as due - deliberately fires once
    // shortly after every service start/restart (an agent that was offline is exactly the case
    // reconciliation exists to catch), not just on the configured interval.
    private bool IsReconciliationDue(DlpPolicy policy)
    {
        if (_lastReconciliationAtUtc is null) return true;
        var interval = TimeSpan.FromMinutes(Math.Max(1, policy.FileInventorySync.ReconciliationScanIntervalMinutes));
        return DateTimeOffset.UtcNow - _lastReconciliationAtUtc.Value >= interval;
    }

    // Loops until the outbox is drained (not just one batch) - the initial sync and reconciliation
    // passes can enqueue far more than one BatchSize's worth in a single walk, and both want to push
    // everything out promptly rather than trickle it out one batch per IncrementalSyncSeconds tick.
    private async Task DrainAndSendAsync(string syncKind, DlpPolicy policy, CancellationToken cancellationToken)
    {
        while (true)
        {
            var items = await outbox.ReadBatchAsync(policy.FileInventorySync.BatchSize, cancellationToken);
            if (items.Count == 0) return;

            var identity = identityProvider.Get();
            var response = await backendApiClient.SendFileInventoryBatchAsync(new AgentFileInventoryBatchRequest
            {
                TenantId = identity.TenantId,
                DeviceId = identity.DeviceId,
                AgentVersion = identity.AgentVersion,
                SyncKind = syncKind,
                Changes = items.Select(item => item.Change).ToList()
            }, cancellationToken);

            var delivered = response.AcceptedChangeIds.ToHashSet();
            outbox.MarkDelivered(items, delivered);

            foreach (var rejection in response.RejectedChanges.Where(item => !item.Retryable))
            {
                var item = items.FirstOrDefault(candidate => candidate.Change.ChangeId == rejection.ChangeId);
                if (item is not null) outbox.MarkPermanentlyRejected(item, rejection.ReasonCode);
            }

            if (items.Count < policy.FileInventorySync.BatchSize) return; // that was the last page
        }
    }
}
