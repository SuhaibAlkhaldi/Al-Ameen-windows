using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// Pulls the tenant's admin-configured classification rules down to this device on the same cadence
// as policy sync, and persists them via DictionaryRuleStore for LocalAiFileClassificationProvider to
// read on every classification - a direct copy of PolicySyncWorker's loop/backoff shape, kept as a
// separate worker (rather than folded into policy sync) because dictionary rules change on their own
// schedule, independent of the rest of DlpPolicy.
public sealed class DictionaryRuleSyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    BackendApiClient backendApiClient,
    DictionaryRuleStore dictionaryRuleStore,
    AuditLogger auditLogger,
    PolicyRefreshSignal refreshSignal,
    ILogger<DictionaryRuleSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (policyStore.Get().Backend.Enabled)
                    await SynchronizeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Dictionary rule synchronization failed. The last valid local/cached rules remain active.");
            }

            var policy = policyStore.Get();
            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.Backend.PolicySyncSeconds, 5, 3600));
            try
            {
                await refreshSignal.WaitAsync(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        var identity = identityProvider.Get();
        var response = await backendApiClient.GetDictionaryRulesAsync(identity, cancellationToken);
        if (response is null || response.Version <= dictionaryRuleStore.Get().Version) return;

        dictionaryRuleStore.Set(response);
        logger.LogInformation("Dictionary rules updated to version {Version} ({RuleCount} rules).", response.Version, response.Rules.Count);
        await auditLogger.WriteAsync(new AuditEvent
        {
            ActionKey = "dictionary-rules.apply",
            EventType = "DictionaryRulesApplied",
            Action = "remote-dictionary-rules",
            Result = "succeeded",
            ReasonCode = "NewerVersionPulled",
            Details = $"Version={response.Version}; RuleCount={response.Rules.Count}"
        }, cancellationToken);
    }
}
