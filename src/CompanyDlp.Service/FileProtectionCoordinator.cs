using System.Collections.Concurrent;
using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Extensions.Logging;

namespace CompanyDlp.Service;

public sealed class FileProtectionCoordinator(
    PolicyStore policyStore,
    AgentIdentityProvider identityProvider,
    PermissionEvaluator permissionEvaluator,
    FileProtectionEngine engine,
    AuditLogger auditLogger,
    FileClassificationCache classificationCache,
    FileClassificationService classificationService,
    EncryptedFileHashStore encryptedFileHashStore,
    ILogger<FileProtectionCoordinator> logger)
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

        // Decrypt is gated by the classification tier of the ORIGINAL plaintext, which can only be
        // resolved from the .dlpenc header's fileId (recorded in EncryptedFileHashStore at encrypt
        // time - see below). A path that isn't an existing .dlpenc file at all leaves decryptFileHash
        // null (this isn't a real decrypt-of-a-classified-file scenario - nothing to gate). But once
        // we've confirmed this IS a real .dlpenc file, its classification must always resolve to
        // something non-null - a real hash for a file encrypted through this store, or a deterministic
        // per-fileId sentinel otherwise (old files predating this store, or a lost local record) - so
        // it is NEVER evaluated as "no classification context" and silently default-allowed. Old files
        // aren't a special case: the sentinel is guaranteed to miss FileClassificationCache (it isn't a
        // real SHA-256), so PermissionEvaluator's existing cache-miss convention (fail-closed to
        // ClassificationTiers.VerySecret) gates them exactly like any other unclassified file - no new
        // evaluator logic, and no dependency on recovering history that may not even still be around
        // (audit retention, or a file that was genuinely never classified to begin with).
        string? decryptFileHash = null;
        if (isDecrypt && !string.IsNullOrWhiteSpace(request.FilePath))
        {
            try
            {
                var peekPath = Path.GetFullPath(request.FilePath);
                if (File.Exists(peekPath) && peekPath.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase))
                {
                    var fileId = await engine.PeekFileIdAsync(peekPath, cancellationToken);
                    var localEntry = encryptedFileHashStore.TryGet(fileId);
                    decryptFileHash = localEntry?.FileHash ?? EncryptedFileHashStore.UnresolvedClassificationSentinel(fileId);
                }
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not read the .dlpenc header to resolve classification for {Path}; evaluating without file context.", request.FilePath);
            }
        }

        var decision = permissionEvaluator.Evaluate(
            policyStore.Get(),
            actionKey,
            context,
            identityProvider.Get(),
            DateTimeOffset.UtcNow,
            decryptFileHash);

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

            if (!isDecrypt)
            {
                // Must happen BEFORE encryption, not after - EncryptAndDeleteOriginalAsync deletes the
                // plaintext once verified, and classification (a real AI call) needs the actual file
                // bytes. Reuses FileClassificationCache/FileClassificationService exactly like the
                // background scanner does, so a file the scanner already classified (the common case -
                // encryption targets are typically already sitting in a watched folder) is a no-op here.
                await EnsureClassifiedBeforeEncryptAsync(fullPath, context, cancellationToken);
            }

            var result = isDecrypt
                ? await engine.DecryptAsync(fullPath, cancellationToken)
                : await engine.EncryptAndDeleteOriginalAsync(fullPath, cancellationToken);

            if (!isDecrypt)
            {
                // The plaintext hash is only ever known at this moment (encrypt time) - persist the
                // fileId->hash association now so a later decrypt can resolve classification from the
                // header's fileId alone, without needing the (by then deleted) plaintext.
                encryptedFileHashStore.Set(new EncryptedFileHashEntry(
                    result.FileId, result.OriginalSha256.ToLowerInvariant(), DateTimeOffset.UtcNow));
            }

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

    // Mirrors FileInventoryScanner.ClassifyIfNeededAsync's cache-check-then-classify pattern: hash the
    // file, skip the (potentially remote) classification call entirely on a cache hit, otherwise
    // classify with the real file content and cache the result under the same hash key the background
    // scanner uses. Best-effort: a classification failure here must never block the encrypt itself
    // (the file simply stays unclassified, and PermissionEvaluator's existing fail-closed convention -
    // an unresolvable classification ranks as VerySecret - takes over at decrypt time instead).
    private async Task EnsureClassifiedBeforeEncryptAsync(string fullPath, ClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            string hash;
            await using (var hashStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                hash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)).ToLowerInvariant();
            }

            // A cached result from a provisional path (BlockAll stub, AI provider unreachable at the
            // time, etc.) never counts as a genuine answer - same convention FileInventoryScanner
            // already applies - so it gets a real attempt now instead of being stuck with a stale
            // placeholder forever (e.g. a transient network blip during a previous encrypt of this
            // exact content).
            var cached = classificationCache.TryGet(hash);
            if (cached is not null && !FileClassificationReasonCodes.TransientFailureReasonCodes.Contains(cached.ReasonCode)) return;

            var info = new FileInfo(fullPath);
            var request = new FileClassificationRequest
            {
                FileName = info.Name,
                Extension = info.Extension,
                SizeBytes = info.Length,
                Sha256 = hash,
                Channel = "file-encrypt",
                Destination = ""
            };

            await using var contentStream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var result = await classificationService.ClassifyAsync(request, context, cancellationToken, contentStream);
            classificationCache.Set(new CachedFileClassification(hash, result.Classification, result.ReasonCode, DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not classify {Path} before encryption; it will be treated as unclassified (fail-closed) at decrypt time.", fullPath);
        }
    }
}
