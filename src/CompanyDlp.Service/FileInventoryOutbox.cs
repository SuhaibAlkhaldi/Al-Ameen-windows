using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed record FileInventoryOutboxItem(string Path, FileInventoryChangeEnvelope Change);

// File-based, disk-backed FIFO queue for outgoing file-inventory changes - a near-mirror of
// AuditOutbox (same enqueue-locally/drain-in-batches/mark-delivered-by-ack shape), so a batch that
// fails to send (network blip, backend restart) just sits in `pending/` for the next
// FileInventorySyncWorker tick to retry, instead of being lost. Not driven through PipeServer's
// RunAsClient impersonation the way audit events sometimes are (see AuditOutbox.EnqueueAsync's
// RevertToSelf comment) - every writer here (FileInventoryChangeWatcher, the initial-sync and
// reconciliation runners) is plain BackgroundService code running as the service's own identity, so
// that specific impersonation pitfall doesn't apply and isn't worked around here.
public sealed class FileInventoryOutbox(
    PolicyStore policyStore,
    MachineDataProtector protector,
    ILogger<FileInventoryOutbox> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastSuccessfulSyncAtUtc;
    private string _lastSyncError = "";

    private string RootDirectory => ResolveRootDirectory();
    private string PendingDirectory => Path.Combine(RootDirectory, "pending");
    private string DeadLetterDirectory => Path.Combine(RootDirectory, "dead-letter");

    public async Task EnqueueAsync(FileInventoryChangeEnvelope change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var clear = JsonSerializer.SerializeToUtf8Bytes(change, JsonDefaults.Options);
        var encrypted = protector.Protect(clear);
        // Ticks-then-guid filename gives FIFO delivery order for free via a plain directory listing -
        // same trick AuditOutbox uses - without needing a separate index/checkpoint file.
        var fileName = $"{change.OccurredAtUtc.UtcDateTime.Ticks:D19}-{change.ChangeId:N}.evt";
        var destination = Path.Combine(PendingDirectory, fileName);
        var temporary = destination + ".tmp";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(PendingDirectory);

            const int maxAttempts = 3;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
                    File.Move(temporary, destination, false);
                    return;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < maxAttempts)
                {
                    logger.LogWarning(exception, "File inventory outbox write/rename attempt {Attempt} failed for {FileName}; retrying.", attempt, fileName);
                    TryDelete(temporary);
                    await Task.Delay(150 * attempt, cancellationToken);
                }
            }
        }
        finally
        {
            TryDelete(temporary);
            _gate.Release();
            CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    public async Task<IReadOnlyList<FileInventoryOutboxItem>> ReadBatchAsync(int maximumCount, CancellationToken cancellationToken)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 500);
        if (!Directory.Exists(PendingDirectory)) return [];

        var items = new List<FileInventoryOutboxItem>();
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
                    var change = JsonSerializer.Deserialize<FileInventoryChangeEnvelope>(clear, JsonDefaults.Options);
                    if (change is null) throw new JsonException("File inventory change payload was empty.");
                    items.Add(new FileInventoryOutboxItem(path, change));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "File inventory outbox item {Path} is unreadable and will be moved to dead-letter.", path);
                MovePathToDeadLetter(path, "unreadable");
            }
        }
        return items;
    }

    public void MarkDelivered(IEnumerable<FileInventoryOutboxItem> items, ISet<Guid> deliveredChangeIds)
    {
        foreach (var item in items.Where(item => deliveredChangeIds.Contains(item.Change.ChangeId)))
            TryDelete(item.Path);
        _lastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        _lastSyncError = "";
    }

    public void MarkPermanentlyRejected(FileInventoryOutboxItem item, string reasonCode) =>
        MovePathToDeadLetter(item.Path, SanitizeFilePart(reasonCode));

    public void RecordSyncError(string message) => _lastSyncError = Sanitize(message, 1000);

    public FileInventoryOutboxStatus GetStatus() => new()
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
        return Path.Combine(root, "CompanyDlp", "FileInventoryOutbox");
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
            logger.LogError(exception, "Could not move file inventory outbox item {Path} to dead-letter.", path);
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

public sealed class FileInventoryOutboxStatus
{
    public int PendingCount { get; set; }
    public int DeadLetterCount { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }
    public string LastSyncError { get; set; } = "";
}
