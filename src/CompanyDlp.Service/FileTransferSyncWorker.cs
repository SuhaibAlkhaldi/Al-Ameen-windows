using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

// Drains FileTransferOutbox to the backend (Phase 7). Mirrors FileInventorySyncWorker's loop: it sends in batches until the
// outbox is empty, and a failed request leaves everything in place for the next tick. Does nothing unless the backend policy
// is enabled AND the organization has turned on file transfer observation.
public sealed class FileTransferSyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    FileTransferOutbox outbox,
    BackendApiClient backendApiClient,
    ILogger<FileTransferSyncWorker> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();

            try
            {
                if (policy.Enabled && policy.Backend.Enabled && policy.FileTransferObservation.Enabled)
                {
                    await DrainAsync(policy, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                outbox.RecordSyncError($"{exception.GetType().Name}: {exception.Message}");
                logger.LogWarning(exception, "File transfer observation sync failed; observations remain in the encrypted outbox.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task DrainAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var batchSize = policy.FileTransferObservation.BatchSize;

        while (true)
        {
            var items = await outbox.ReadBatchAsync(batchSize, cancellationToken);
            if (items.Count == 0) return;

            var identity = identityProvider.Get();
            var ack = await backendApiClient.SendFileTransferBatchAsync(new FileTransferEventBatchRequest
            {
                TenantId = identity.TenantId,
                DeviceId = identity.DeviceId,
                Events = items.Select(item => item.Observation).ToList()
            }, cancellationToken);

            if (!ack.Success)
            {
                throw new InvalidOperationException("The backend did not accept the file transfer batch.");
            }

            outbox.MarkBatchDelivered(items);

            if (items.Count < batchSize) return; // that was the last page
        }
    }
}
