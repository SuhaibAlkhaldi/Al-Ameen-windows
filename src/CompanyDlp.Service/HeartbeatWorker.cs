using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class HeartbeatWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    AuditOutbox outbox,
    BackendApiClient backendApiClient,
    TrustedClock trustedClock,
    PolicyRefreshSignal refreshSignal,
    ILogger<HeartbeatWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            try
            {
                if (policy.Backend.Enabled)
                {
                    var identity = identityProvider.Get();
                    var status = outbox.GetStatus();
                    var response = await backendApiClient.SendHeartbeatAsync(new AgentHeartbeatRequest
                    {
                        TenantId = identity.TenantId,
                        DeviceId = identity.DeviceId,
                        MachineName = identity.MachineName,
                        AgentVersion = identity.AgentVersion,
                        OsVersion = Environment.OSVersion.VersionString,
                        LastAppliedPolicyVersion = policyStore.CurrentRemoteVersion,
                        PendingAuditEventCount = status.PendingCount
                    }, stoppingToken);
                    trustedClock.ObserveServerTime(response.ServerTimeUtc);
                    if (response.PolicyRefreshRequired)
                        refreshSignal.RequestRefresh();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Company DLP heartbeat failed.");
            }

            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.Backend.HeartbeatSeconds, 10, 3600));
            await Task.Delay(delay, stoppingToken);
        }
    }
}
