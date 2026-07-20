using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class AuditLogger(
    PolicyStore policyStore,
    SecurityEventFactory eventFactory,
    AuditOutbox outbox,
    ILogger<AuditLogger> logger)
{
    private readonly SemaphoreSlim _fileGate = new(1, 1);

    public Task WriteAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
        WriteAsync(auditEvent, null, cancellationToken);

    public async Task WriteAsync(
        AuditEvent auditEvent,
        ClientContext? context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var securityEvent = eventFactory.Create(auditEvent, context);
            await outbox.EnqueueAsync(securityEvent, cancellationToken);

            if (policyStore.Get().Runtime.Mode.Equals("Development", StringComparison.OrdinalIgnoreCase))
                await WriteReadableDevelopmentLogAsync(securityEvent, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to persist a Company DLP security event.");
        }
    }

    private async Task WriteReadableDevelopmentLogAsync(SecurityEventEnvelope securityEvent, CancellationToken cancellationToken)
    {
        var directory = ResolveReadableAuditDirectory();
        var path = Path.Combine(directory, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
        var line = JsonSerializer.Serialize(securityEvent, JsonDefaults.Options).Replace(Environment.NewLine, string.Empty) + Environment.NewLine;
        Directory.CreateDirectory(directory);

        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(path, line, cancellationToken);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private string ResolveReadableAuditDirectory()
    {
        var configured = policyStore.Get().Runtime.AuditDirectory;
        if (!string.IsNullOrWhiteSpace(configured)) return Environment.ExpandEnvironmentVariables(configured);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompanyDlp", "Audit");
    }
}
