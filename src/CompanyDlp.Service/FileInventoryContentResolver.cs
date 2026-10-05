using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Service;

public sealed record FileInventoryContent(string FileHash, string ClassificationTier, string Extension);

// Resolves "what does the File Inventory Report need to know about this path's current content" for
// both a plaintext file (hash it directly) and an encrypted .dlpenc file (resolved via the same
// fileId -> original-plaintext-hash -> classification chain FileProtectionCoordinator already uses
// for file.open-access decrypt decisions - EncryptedFileHashStore exists precisely so a .dlpenc's
// classification can be recovered without the plaintext bytes).
//
// Without this, a received file that gets auto-encrypted (see FileInventoryScanner.
// ApplyAutoProtectionIfEnabled) flips from a supported extension (e.g. .docx) to .dlpenc -
// DocumentTextExtractor.IsSupported correctly rejects that extension for classification purposes,
// but treating that rejection as "not trackable" would make the file vanish from the File Inventory
// Report at the exact moment its Protection status becomes the most interesting thing about it.
// IsTrackable/ResolveAsync are the two things FileInventoryChangeWatcher, FileInventoryInitialSyncRunner,
// and FileInventoryReconciliationRunner all need identically, so this logic lives in exactly one place.
public sealed class FileInventoryContentResolver(
    FileClassificationCache classificationCache,
    EncryptedFileHashStore encryptedFileHashStore,
    FileProtectionEngine fileProtectionEngine,
    ILogger<FileInventoryContentResolver> logger)
{
    private const string EncryptedExtension = ".dlpenc";

    // True for the 8 extensions DocumentTextExtractor can classify, PLUS .dlpenc - a file this
    // system has ever had a classification opinion about, encrypted or not.
    public static bool IsTrackable(string path)
    {
        var extension = Path.GetExtension(path);
        return DocumentTextExtractor.IsSupported(extension) || extension.Equals(EncryptedExtension, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FileInventoryContent?> ResolveAsync(string path, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path);

        return extension.Equals(EncryptedExtension, StringComparison.OrdinalIgnoreCase)
            ? await ResolveEncryptedAsync(path, cancellationToken)
            : ResolvePlaintext(path, extension);
    }

    private FileInventoryContent? ResolvePlaintext(string path, string extension)
    {
        if (!DocumentTextExtractor.IsSupported(extension)) return null;

        var hash = ComputeHash(path);
        if (hash is null) return null;

        var classification = classificationCache.TryGet(hash);
        if (!IsRealClassification(classification)) return null;

        return new FileInventoryContent(hash, classification!.Classification, extension.TrimStart('.'));
    }

    private async Task<FileInventoryContent?> ResolveEncryptedAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var fileId = await fileProtectionEngine.PeekFileIdAsync(path, cancellationToken);
            var entry = encryptedFileHashStore.TryGet(fileId);
            if (entry is null) return null; // no local record of this ciphertext's original content yet

            var classification = classificationCache.TryGet(entry.FileHash);
            if (!IsRealClassification(classification)) return null;

            // Underlying document type, without the .dlpenc suffix - the report shows this file's real
            // type (e.g. "docx") even while it's encrypted, not the meaningless "dlpenc".
            var innerPath = path[..^EncryptedExtension.Length];
            var innerExtension = Path.GetExtension(innerPath).TrimStart('.');

            return new FileInventoryContent(entry.FileHash, classification!.Classification, innerExtension);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not resolve the original content for encrypted file {Path}.", path);
            return null;
        }
    }

    // A cached entry whose ReasonCode is one of FileClassificationReasonCodes.TransientFailureReasonCodes
    // (e.g. NoFileContentAvailableForAiClassification for an empty/near-empty file) is not a settled
    // verdict - FileInventoryScanner itself never trusts one of these as final and keeps re-attempting
    // classification every tick (see its ProvisionalReasonCodes check) rather than tagging/watermarking
    // the file. The File Inventory Report must treat it exactly the same way - reporting a placeholder
    // like "ProviderUnavailable" as if it were a real classification tier would corrupt the report's
    // Classification column/filter/sort for a file that was never actually classified.
    private static bool IsRealClassification(CachedFileClassification? classification) =>
        classification is not null && !FileClassificationReasonCodes.TransientFailureReasonCodes.Contains(classification.ReasonCode);

    public static string? ComputeHash(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
