using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed record AuditOutboxItem(string Path, SecurityEventEnvelope Event);

public sealed class AuditOutbox(
    PolicyStore policyStore,
    MachineDataProtector protector,
    ILogger<AuditOutbox> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastSuccessfulSyncAtUtc;
    private string _lastSyncError = "";

    private string RootDirectory => ResolveRootDirectory();
    private string PendingDirectory => Path.Combine(RootDirectory, "pending");
    private string DeadLetterDirectory => Path.Combine(RootDirectory, "dead-letter");

    public async Task EnqueueAsync(SecurityEventEnvelope securityEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(securityEvent);
        var clear = JsonSerializer.SerializeToUtf8Bytes(securityEvent, JsonDefaults.Options);
        var encrypted = protector.Protect(clear);
        var fileName = $"{securityEvent.OccurredAtUtc.UtcDateTime.Ticks:D19}-{securityEvent.EventId:N}.evt";
        var destination = Path.Combine(PendingDirectory, fileName);
        var temporary = destination + ".tmp";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(PendingDirectory);
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
            File.Move(temporary, destination, false);
        }
        finally
        {
            TryDelete(temporary);
            _gate.Release();
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<IReadOnlyList<AuditOutboxItem>> ReadBatchAsync(int maximumCount, CancellationToken cancellationToken)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 500);
        if (!Directory.Exists(PendingDirectory)) return [];

        var items = new List<AuditOutboxItem>();
        foreach (var path in Directory.EnumerateFiles(PendingDirectory, "*.evt")
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                     .Take(maximumCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken);
                var clear = protector.Unprotect(protectedBytes);
                try
                {
                    var securityEvent = JsonSerializer.Deserialize<SecurityEventEnvelope>(clear, JsonDefaults.Options);
                    if (securityEvent is null) throw new JsonException("Audit event payload was empty.");
                    items.Add(new AuditOutboxItem(path, securityEvent));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Audit outbox item {Path} is unreadable and will be moved to dead-letter.", path);
                MovePathToDeadLetter(path, "unreadable");
            }
        }
        return items;
    }

    public void MarkDelivered(IEnumerable<AuditOutboxItem> items, ISet<Guid> deliveredEventIds)
    {
        foreach (var item in items.Where(item => deliveredEventIds.Contains(item.Event.EventId)))
            TryDelete(item.Path);
        _lastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        _lastSyncError = "";
    }

    public void MarkPermanentlyRejected(AuditOutboxItem item, string reasonCode) =>
        MovePathToDeadLetter(item.Path, SanitizeFilePart(reasonCode));

    public void RecordSyncError(string message) => _lastSyncError = Sanitize(message, 1000);

    public AuditOutboxStatus GetStatus() => new()
    {
        PendingCount = Directory.Exists(PendingDirectory) ? Directory.EnumerateFiles(PendingDirectory, "*.evt").Count() : 0,
        DeadLetterCount = Directory.Exists(DeadLetterDirectory) ? Directory.EnumerateFiles(DeadLetterDirectory, "*.evt").Count() : 0,
        LastSuccessfulSyncAtUtc = _lastSuccessfulSyncAtUtc,
        LastSyncError = _lastSyncError
    };

    private string ResolveRootDirectory()
    {
        var mode = policyStore.Get().Runtime.Mode;
        var root = mode.Equals("Production", StringComparison.OrdinalIgnoreCase)
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CompanyDlp", "Outbox");
    }

    private void MovePathToDeadLetter(string path, string reason)
    {
        try
        {
            Directory.CreateDirectory(DeadLetterDirectory);
            var target = Path.Combine(DeadLetterDirectory, $"{Path.GetFileNameWithoutExtension(path)}-{reason}.evt");
            File.Move(path, target, true);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not move audit outbox item {Path} to dead-letter.", path);
        }
    }

    private static string SanitizeFilePart(string value)
    {
        var cleaned = string.Concat((value ?? "rejected").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        return string.IsNullOrWhiteSpace(cleaned) ? "rejected" : cleaned[..Math.Min(cleaned.Length, 50)];
    }

    private static string Sanitize(string? value, int maximumLength)
    {
        var cleaned = (value ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= maximumLength ? cleaned : cleaned[..maximumLength];
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
