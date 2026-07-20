using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class PolicySyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    BackendApiClient backendApiClient,
    PolicySnapshotValidator validator,
    AuditLogger auditLogger,
    ILogger<PolicySyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.Backend.PolicySyncSeconds, 5, 3600));
            try
            {
                if (policy.Backend.Enabled)
                    await SynchronizeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Central DLP policy synchronization failed. The last valid local/cached policy remains active.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        var identity = identityProvider.Get();
        var snapshot = await backendApiClient.GetPolicyAsync(identity, policyStore.CurrentRemoteVersion, cancellationToken);
        if (snapshot is null || snapshot.Version <= policyStore.CurrentRemoteVersion) return;

        if (!validator.TryValidate(snapshot, identity, DateTimeOffset.UtcNow, out var failureReason))
        {
            await auditLogger.WriteAsync(new AuditEvent
            {
                ActionKey = "policy.apply",
                EventType = "PolicyRejected",
                Action = "remote-policy",
                Result = "blocked",
                ReasonCode = failureReason,
                Details = $"PolicyId={snapshot.PolicyId:D}; Version={snapshot.Version}"
            }, cancellationToken);
            return;
        }

        policyStore.ApplyRemoteSnapshot(snapshot);
        await auditLogger.WriteAsync(new AuditEvent
        {
            ActionKey = "policy.apply",
            EventType = "PolicyApplied",
            Action = "remote-policy",
            Result = "succeeded",
            ReasonCode = "ValidSignedPolicy",
            Details = $"PolicyId={snapshot.PolicyId:D}; Version={snapshot.Version}"
        }, cancellationToken);
    }
}
