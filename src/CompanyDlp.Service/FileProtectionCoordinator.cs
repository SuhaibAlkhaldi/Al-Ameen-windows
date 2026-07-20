using System.Collections.Concurrent;
using CompanyDlp.Contracts;

namespace CompanyDlp.Service;

public sealed class FileProtectionCoordinator(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    PermissionEvaluator permissionEvaluator,
    FileProtectionEngine engine,
    AuditLogger auditLogger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FileProtectionResponse> ExecuteAsync(
        FileProtectionRequest request,
        ClientContext context,
        CancellationToken cancellationToken)
    {
        var transactionId = Guid.NewGuid();
        var normalizedAction = request.Action?.Trim().ToLowerInvariant();
        if (normalizedAction is not ("encrypt" or "decrypt"))
        {
            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = false,
                ErrorCode = "UnsupportedAction",
                Message = "The file protection action must be either encrypt or decrypt."
            };
        }

        var isDecrypt = normalizedAction == "decrypt";
        var actionKey = isDecrypt ? ActionKeys.FileDecrypt : ActionKeys.FileEncrypt;
        var decision = permissionEvaluator.Evaluate(
            policyStore.Get(),
            actionKey,
            context,
            identityProvider.Get(),
            DateTimeOffset.UtcNow);

        var fileName = Path.GetFileName(request.FilePath ?? "");
        if (!decision.IsAllowed)
        {
            await auditLogger.WriteAsync(new AuditEvent
            {
                CorrelationId = transactionId,
                ActionKey = actionKey,
                EventType = isDecrypt ? "DecryptionBlocked" : "EncryptionBlocked",
                Action = isDecrypt ? "decrypt" : "encrypt",
                Result = "blocked",
                ReasonCode = decision.ReasonCode,
                PermissionGrantId = decision.PermissionGrantId,
                ResourceName = fileName,
                ResourceExtension = Path.GetExtension(fileName),
                SourceProcessName = context.ClientName
            }, context, cancellationToken);

            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = false,
                ErrorCode = "PermissionDenied",
                Message = "This file protection action is not allowed by the effective Company DLP policy."
            };
        }

        if (string.IsNullOrWhiteSpace(request.FilePath))
        {
            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = false,
                ErrorCode = "InvalidPath",
                Message = "A non-empty file path is required."
            };
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(request.FilePath);
        }
        catch (Exception exception)
        {
            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = false,
                ErrorCode = "InvalidPath",
                Message = exception.Message
            };
        }

        var gate = _fileLocks.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            await auditLogger.WriteAsync(new AuditEvent
            {
                CorrelationId = transactionId,
                ActionKey = actionKey,
                EventType = isDecrypt ? "DecryptionStarted" : "EncryptionStarted",
                Action = isDecrypt ? "decrypt" : "encrypt",
                Result = "audit",
                ReasonCode = decision.ReasonCode,
                PermissionGrantId = decision.PermissionGrantId,
                ResourceName = Path.GetFileName(fullPath),
                ResourceExtension = Path.GetExtension(fullPath),
                ResourceSizeBytes = File.Exists(fullPath) ? new FileInfo(fullPath).Length : null,
                SourceProcessName = context.ClientName
            }, context, cancellationToken);

            var result = isDecrypt
                ? await engine.DecryptAsync(fullPath, cancellationToken)
                : await engine.EncryptAndDeleteOriginalAsync(fullPath, cancellationToken);

            await auditLogger.WriteAsync(new AuditEvent
            {
                CorrelationId = transactionId,
                ActionKey = actionKey,
                EventType = isDecrypt ? "DecryptionSucceeded" : "EncryptionSucceeded",
                Action = isDecrypt ? "decrypt" : "encrypt",
                Result = "succeeded",
                ReasonCode = decision.ReasonCode,
                PermissionGrantId = decision.PermissionGrantId,
                ResourceName = Path.GetFileName(fullPath),
                ResourceExtension = Path.GetExtension(fullPath),
                ResourceSizeBytes = result.OriginalSizeBytes,
                ResourceSha256 = result.OriginalSha256,
                Details = $"fileId={result.FileId:D}; output={Path.GetFileName(result.OutputPath)}; outputSha256={result.OutputSha256}",
                SourceProcessName = context.ClientName
            }, context, cancellationToken);

            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = true,
                OutputPath = result.OutputPath,
                Message = isDecrypt
                    ? "The file was decrypted successfully."
                    : "The file was encrypted, authenticated, verified, and the plaintext was deleted according to policy."
            };
        }
        catch (Exception exception)
        {
            await auditLogger.WriteAsync(new AuditEvent
            {
                CorrelationId = transactionId,
                ActionKey = actionKey,
                EventType = isDecrypt ? "DecryptionFailed" : "EncryptionFailed",
                Action = isDecrypt ? "decrypt" : "encrypt",
                Result = "failed",
                ReasonCode = exception.GetType().Name,
                PermissionGrantId = decision.PermissionGrantId,
                ResourceName = Path.GetFileName(fullPath),
                ResourceExtension = Path.GetExtension(fullPath),
                Details = exception.GetType().Name,
                SourceProcessName = context.ClientName
            }, context, cancellationToken);

            return new FileProtectionResponse
            {
                TransactionId = transactionId,
                Success = false,
                ErrorCode = exception.GetType().Name,
                Message = exception.Message
            };
        }
        finally
        {
            gate.Release();
        }
    }
}
