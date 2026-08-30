using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

// The network half of the FileWatermarkDisable escrow flow (see WatermarkEscrowStore's class
// comment for the full design). FileInventoryScanner never talks to the backend itself - it only
// ever creates local escrow records and flags RestoreRequested; this worker is the only thing that
// actually performs the two round trips involved (wrap a freshly-generated DEK, or unwrap one to
// restore a corner-only render), on its own cadence, exactly like AuditSyncWorker is the only thing
// that actually delivers AuditOutbox's queued events. Splitting it out this way keeps a scan tick
// (which can walk thousands of files) from ever blocking on network I/O for this feature.
public sealed class WatermarkEscrowSyncWorker(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    WatermarkEscrowStore escrowStore,
    BackendApiClient backendApiClient,
    ILogger<WatermarkEscrowSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var policy = policyStore.Get();
            // Reuses Backend.AuditSyncSeconds rather than introducing a third sync-interval policy
            // field - both are "how often does this agent talk to the backend about something
            // queued locally" concerns, and there is no product reason for them to run on different
            // cadences.
            var delay = TimeSpan.FromSeconds(Math.Clamp(policy.Backend.AuditSyncSeconds, 2, 3600));
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
                logger.LogWarning(exception, "Watermark escrow synchronization failed; will retry on the next cycle.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        var identity = identityProvider.Get();
        var records = escrowStore.GetAll();

        // Phase 1: upload any DEK the backend hasn't taken custody of yet. Each record's plaintext
        // key only ever exists locally (DPAPI-protected) between CreateFromPristineBytes and this
        // call succeeding - see WatermarkEscrowStore's class comment.
        foreach (var record in records.Where(item => !item.KeyWrapped))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plainDek = escrowStore.TryReadPendingPlainKey(record.EscrowId);
            if (plainDek is null) continue; // already wrapped-and-deleted, or unreadable - next load will retry

            try
            {
                var response = await backendApiClient.WrapFileKeyAsync(new FileKeyWrapRequest
                {
                    TenantId = identity.TenantId,
                    DeviceId = identity.DeviceId,
                    FileId = record.EscrowId,
                    PlainKeyBase64 = Convert.ToBase64String(plainDek)
                }, cancellationToken);

                escrowStore.MarkKeyWrapped(record.EscrowId, response.KeyId, response.WrappedKeyBase64);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not upload the watermark escrow key for {EscrowId}; will retry.", record.EscrowId);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainDek);
            }
        }

        // Phase 2: restore any record FileInventoryScanner flagged as having an active
        // FileWatermarkDisable grant. Only reachable for records that already made it through
        // Phase 1 (KeyWrapped) - a record whose key hasn't even been wrapped yet obviously has
        // nothing to unwrap; it will be picked up here on a later cycle once Phase 1 catches up.
        foreach (var record in records.Where(item => item.RestoreRequested && item.KeyWrapped && !item.WatermarkHidden))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[]? plainDek = null;
            try
            {
                if (!File.Exists(record.LivePath))
                {
                    // The file moved/was deleted/renamed since the request was flagged - nothing to
                    // restore onto. Clear the request so this doesn't retry forever; a future scan
                    // tick re-flags it if the file reappears at a newly-known path.
                    escrowStore.MarkWatermarkHidden(record.EscrowId, hidden: false);
                    continue;
                }

                var response = await backendApiClient.UnwrapFileKeyAsync(new FileKeyUnwrapRequest
                {
                    TenantId = identity.TenantId,
                    DeviceId = identity.DeviceId,
                    FileId = record.EscrowId,
                    KeyId = record.KeyId!,
                    WrappedKeyBase64 = record.WrappedKeyBase64!
                }, cancellationToken);

                plainDek = Convert.FromBase64String(response.PlainKeyBase64);
                var pristineBytes = escrowStore.DecryptBlob(record.EscrowId, plainDek);

                var temporary = record.LivePath + ".tmp";
                await File.WriteAllBytesAsync(temporary, pristineBytes, cancellationToken);
                File.Move(temporary, record.LivePath, true);

                escrowStore.MarkWatermarkHidden(record.EscrowId, hidden: true);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not restore the pristine (watermark-hidden) file for {EscrowId}; will retry.", record.EscrowId);
            }
            finally
            {
                if (plainDek is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(plainDek);
            }
        }
    }
}
