using System.Security.Cryptography;
using System.Text.Json;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed record FileTransferOutboxItem(string Path, FileTransferObservationNotice Observation);

// File-based, disk-backed FIFO queue for browser send observations (Phase 7). Same shape as FileInventoryOutbox: the
// observation is written encrypted (DPAPI via MachineDataProtector) to pending/, then drained by FileTransferSyncWorker.
// A batch that fails to send stays in pending/ and goes out on the next tick.
//
// Unlike the inventory outbox, marking is per batch. The backend answers with counts, not with the ids it accepted,
// and its decisions are final (accepted, duplicate, or rejected), so a successful response means the whole batch is
// done. A failed response leaves every item in place to retry.
public sealed class FileTransferOutbox(
    PolicyStore policyStore,
    MachineDataProtector protector,
    ILogger<FileTransferOutbox> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset? _lastSuccessfulSyncAtUtc;
    private string _lastSyncError = "";

    private string RootDirectory => ResolveRootDirectory();
    private string PendingDirectory => Path.Combine(RootDirectory, "pending");
    private string DeadLetterDirectory => Path.Combine(RootDirectory, "dead-letter");

    public async Task EnqueueAsync(FileTransferObservationNotice observation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var clear = JsonSerializer.SerializeToUtf8Bytes(observation, JsonDefaults.Options);
        var encrypted = protector.Protect(clear);

        // Ticks first, then a fresh GUID: plain directory order is FIFO, and two observations never share a name.
        var fileName = $"{observation.ObservedAtUtc.UtcDateTime.Ticks:D19}-{Guid.NewGuid():N}.evt";
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
                    logger.LogWarning(exception, "File transfer outbox write/rename attempt {Attempt} failed; retrying.", attempt);
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

    public async Task<IReadOnlyList<FileTransferOutboxItem>> ReadBatchAsync(int maximumCount, CancellationToken cancellationToken)
    {
        maximumCount = Math.Clamp(maximumCount, 1, 500);
        if (!Directory.Exists(PendingDirectory)) return [];

        var items = new List<FileTransferOutboxItem>();
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
                    var observation = JsonSerializer.Deserialize<FileTransferObservationNotice>(clear, JsonDefaults.Options);
                    if (observation is null) throw new JsonException("File transfer observation payload was empty.");
                    items.Add(new FileTransferOutboxItem(path, observation));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(clear);
                    CryptographicOperations.ZeroMemory(protectedBytes);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "File transfer outbox item {Path} is unreadable and will be moved to dead-letter.", path);
                MovePathToDeadLetter(path, "unreadable");
            }
        }
        return items;
    }

    // Called only after the backend has answered the whole batch successfully.
    public void MarkBatchDelivered(IEnumerable<FileTransferOutboxItem> items)
    {
        foreach (var item in items)
            TryDelete(item.Path);
        _lastSuccessfulSyncAtUtc = DateTimeOffset.UtcNow;
        _lastSyncError = "";
    }

    public void RecordSyncError(string message) => _lastSyncError = Sanitize(message, 1000);

    public FileTransferOutboxStatus GetStatus() => new()
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
        return Path.Combine(root, "CompanyDlp", "FileTransferOutbox");
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
            logger.LogError(exception, "Could not move a file transfer outbox item to dead-letter.");
        }
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

public sealed class FileTransferOutboxStatus
{
    public int PendingCount { get; set; }
    public int DeadLetterCount { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAtUtc { get; set; }
    public string LastSyncError { get; set; } = "";
}
