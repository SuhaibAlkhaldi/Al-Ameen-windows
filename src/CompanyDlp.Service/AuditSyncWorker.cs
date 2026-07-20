using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class AuditSyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    AuditOutbox outbox,
    BackendApiClient backendApiClient,
    ILogger<AuditSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.Backend.AuditSyncSeconds, 2, 3600));
            try
            {
                if (policy.Backend.Enabled)
                    await SynchronizeOnceAsync(policy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var message = $"{exception.GetType().Name}: {exception.Message}";
                outbox.RecordSyncError(message);
                logger.LogWarning(exception, "Company DLP audit synchronization failed; events remain in the encrypted outbox.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SynchronizeOnceAsync(DlpPolicy policy, CancellationToken cancellationToken)
    {
        var items = await outbox.ReadBatchAsync(policy.Backend.AuditBatchSize, cancellationToken);
        if (items.Count == 0) return;

        var identity = identityProvider.Get();
        var response = await backendApiClient.SendAuditBatchAsync(new AuditBatchRequest
        {
            TenantId = identity.TenantId,
            DeviceId = identity.DeviceId,
            AgentVersion = identity.AgentVersion,
            Events = items.Select(item => item.Event).ToList()
        }, cancellationToken);

        var delivered = response.AcceptedEventIds
            .Concat(response.DuplicateEventIds)
            .ToHashSet();
        outbox.MarkDelivered(items, delivered);

        foreach (var rejection in response.RejectedEvents.Where(item => !item.Retryable))
        {
            var item = items.FirstOrDefault(candidate => candidate.Event.EventId == rejection.EventId);
            if (item is not null) outbox.MarkPermanentlyRejected(item, rejection.ReasonCode);
        }
    }
}
