using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CompanyDlp.Contracts;

namespace CompanyDlp.Core;

public sealed record FileProtectionOperationResult(
    Guid FileId,
    string OutputPath,
    long OriginalSizeBytes,
    string OriginalSha256,
    string OutputSha256);

public sealed class FileProtectionEngine(
    PolicyStore policyStore,
    IFileKeyProtector keyProtector)
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CDLPENC2");
    private const byte FormatVersion = 2;
    private const int DefaultChunkSize = 1024 * 1024;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int MaximumFileNameBytes = 4096;
    private const int MaximumProviderBytes = 128;
    private const int MaximumKeyIdBytes = 2048;
    private const int MaximumWrappedKeyBytes = 64 * 1024;

    public async Task<FileProtectionOperationResult> EncryptAndDeleteOriginalAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var fullSourcePath = ValidateSourceFile(sourcePath);
        if (fullSourcePath.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is already a Company DLP encrypted file.");

        var policy = policyStore.Get().FileProtection;
        if (!policy.Enabled) throw new InvalidOperationException("File protection is disabled by policy.");

        var sourceInfo = new FileInfo(fullSourcePath);
        if (sourceInfo.Length > policy.MaximumFileSizeBytes)
            throw new InvalidOperationException("The selected file exceeds the maximum size allowed by policy.");

        var expectedLength = sourceInfo.Length;
        var expectedLastWriteTimeUtc = sourceInfo.LastWriteTimeUtc;
        var expectedHash = await ComputeFileHashAsync(fullSourcePath, cancellationToken);
        var fileId = Guid.NewGuid();
        var dataKey = RandomNumberGenerator.GetBytes(32);
        string? encryptedPath = null;

        try
        {
            var wrappedKey = await keyProtector.WrapAsync(fileId, dataKey, cancellationToken);
            encryptedPath = await EncryptToNewFileAsync(
                fullSourcePath,
                fileId,
                wrappedKey,
                dataKey,
                cancellationToken);

            var currentSourceInfo = new FileInfo(fullSourcePath);
            if (!currentSourceInfo.Exists
                || currentSourceInfo.Length != expectedLength
                || currentSourceInfo.LastWriteTimeUtc != expectedLastWriteTimeUtc)
                throw new IOException("The source file changed while it was being encrypted. The original file was kept.");

            var currentHash = await ComputeFileHashAsync(fullSourcePath, cancellationToken);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(currentHash, expectedHash))
                    throw new IOException("The source file changed while it was being encrypted. The original file was kept.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(currentHash);
            }

            await VerifyEncryptedFileAsync(encryptedPath, expectedHash, expectedLength, cancellationToken);
            var encryptedHash = await ComputeFileHashAsync(encryptedPath, cancellationToken);
            try
            {
                if (policy.DeletePlaintextAfterVerifiedEncryption)
                {
                    File.Delete(fullSourcePath);
                    if (File.Exists(fullSourcePath))
                        throw new IOException("The encrypted file was verified, but Windows could not delete the original plaintext file.");
                }

                return new FileProtectionOperationResult(
                    fileId,
                    encryptedPath,
                    expectedLength,
                    Convert.ToHexString(expectedHash),
                    Convert.ToHexString(encryptedHash));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryptedHash);
            }
        }
        catch
        {
            if (encryptedPath is not null && File.Exists(fullSourcePath)) TryDelete(encryptedPath);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedHash);
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task<FileProtectionOperationResult> DecryptAsync(
        string encryptedPath,
        CancellationToken cancellationToken = default)
    {
        var fullEncryptedPath = ValidateSourceFile(encryptedPath);
        if (!fullEncryptedPath.EndsWith(".dlpenc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected file is not a .dlpenc file.");

        string? temporaryPath = null;
        byte[]? dataKey = null;
        try
        {
            await using var source = new FileStream(
                fullEncryptedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = await ReadHeaderAsync(source, cancellationToken);
            dataKey = await keyProtector.UnwrapAsync(header.FileId, header.WrappedKey, cancellationToken);
            var headerHash = SHA256.HashData(header.Bytes);
            var safeOriginalName = SanitizeFileName(header.OriginalFileName);
            var outputDirectory = Path.GetDirectoryName(fullEncryptedPath)!;
            var outputPath = GetUniquePath(Path.Combine(outputDirectory, safeOriginalName));
            temporaryPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N");

            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                header.ChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var aes = new AesGcm(dataKey, TagLength);
            using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            long totalPlaintext = 0;
            uint chunkIndex = 0;
            var processedRecord = false;
            var ciphertext = new byte[header.ChunkSize];
            var plaintext = new byte[header.ChunkSize];
            var tag = new byte[TagLength];

            try
            {
                do
                {
                    var plaintextLength = await ReadInt32Async(source, cancellationToken);
                    ValidateRecordLength(plaintextLength, header.ChunkSize, totalPlaintext, header.OriginalLength);
                    await ReadExactlyAsync(source, ciphertext.AsMemory(0, plaintextLength), cancellationToken);
                    await ReadExactlyAsync(source, tag, cancellationToken);

                    var nonce = BuildNonce(header.NonceBase, chunkIndex);
                    var additionalData = BuildAdditionalData(headerHash, chunkIndex, plaintextLength);
                    aes.Decrypt(
                        nonce,
                        ciphertext.AsSpan(0, plaintextLength),
                        tag,
                        plaintext.AsSpan(0, plaintextLength),
                        additionalData);

                    if (plaintextLength > 0)
                    {
                        await destination.WriteAsync(plaintext.AsMemory(0, plaintextLength), cancellationToken);
                        plaintextHash.AppendData(plaintext, 0, plaintextLength);
                    }

                    totalPlaintext += plaintextLength;
                    processedRecord = true;
                    chunkIndex = checked(chunkIndex + 1);
                }
                while (totalPlaintext < header.OriginalLength || !processedRecord);

                if (totalPlaintext != header.OriginalLength || source.Position != source.Length)
                    throw new CryptographicException("The encrypted file is incomplete or contains unexpected trailing data.");

                await destination.FlushAsync(cancellationToken);
                destination.Close();
                File.Move(temporaryPath, outputPath);
                temporaryPath = null;

                var outputHash = plaintextHash.GetHashAndReset();
                var encryptedHash = await ComputeFileHashAsync(fullEncryptedPath, cancellationToken);
                try
                {
                    var result = new FileProtectionOperationResult(
                        header.FileId,
                        outputPath,
                        header.OriginalLength,
                        Convert.ToHexString(outputHash),
                        Convert.ToHexString(encryptedHash));

                    if (!policyStore.Get().FileProtection.KeepEncryptedFileAfterDecryption)
                    {
                        File.Delete(fullEncryptedPath);
                        if (File.Exists(fullEncryptedPath))
                            throw new IOException("The file was decrypted, but Windows could not delete the encrypted source file.");
                    }

                    return result;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(outputHash);
                    CryptographicOperations.ZeroMemory(encryptedHash);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(headerHash);
            }
        }
        catch (EndOfStreamException exception)
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
            throw new CryptographicException("The encrypted file is incomplete or damaged.", exception);
        }
        catch
        {
            if (temporaryPath is not null) TryDelete(temporaryPath);
            throw;
        }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private async Task<string> EncryptToNewFileAsync(
        string fullSourcePath,
        Guid fileId,
        WrappedFileKey wrappedKey,
        byte[] dataKey,
        CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(fullSourcePath);
        var outputPath = GetUniquePath(fullSourcePath + ".dlpenc");
        var temporaryPath = outputPath + ".partial-" + Guid.NewGuid().ToString("N");
        var header = BuildHeader(
            fileId,
            wrappedKey,
            Path.GetFileName(fullSourcePath),
            sourceInfo.Length,
            DefaultChunkSize,
            RandomNumberGenerator.GetBytes(NonceLength));
        var headerHash = SHA256.HashData(header.Bytes);

        try
        {
            await using var source = new FileStream(
                fullSourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                DefaultChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            await destination.WriteAsync(header.Bytes, cancellationToken);
            using var aes = new AesGcm(dataKey, TagLength);
            var plaintext = new byte[DefaultChunkSize];
            var ciphertext = new byte[DefaultChunkSize];
            var tag = new byte[TagLength];
            uint chunkIndex = 0;
            var wroteChunk = false;

            try
            {
                while (true)
                {
                    var bytesRead = await ReadChunkAsync(source, plaintext, cancellationToken);
                    if (bytesRead == 0) break;
                    EncryptChunk(
                        aes,
                        header.NonceBase,
                        headerHash,
                        chunkIndex,
                        plaintext.AsSpan(0, bytesRead),
                        ciphertext.AsSpan(0, bytesRead),
                        tag);
                    await WriteRecordAsync(destination, bytesRead, ciphertext.AsMemory(0, bytesRead), tag, cancellationToken);
                    wroteChunk = true;
                    chunkIndex = checked(chunkIndex + 1);
                }

                if (!wroteChunk)
                {
                    EncryptChunk(aes, header.NonceBase, headerHash, 0, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, tag);
                    await WriteRecordAsync(destination, 0, ReadOnlyMemory<byte>.Empty, tag, cancellationToken);
                }

                await destination.FlushAsync(cancellationToken);
                destination.Close();
                File.Move(temporaryPath, outputPath);
                return outputPath;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(headerHash);
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task VerifyEncryptedFileAsync(
        string encryptedPath,
        byte[] expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        byte[]? dataKey = null;
        byte[]? actualHash = null;
        try
        {
            await using var source = new FileStream(
                encryptedPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                DefaultChunkSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = await ReadHeaderAsync(source, cancellationToken);
            if (header.OriginalLength != expectedLength)
                throw new CryptographicException("The encrypted file verification found an unexpected original length.");

            dataKey = await keyProtector.UnwrapAsync(header.FileId, header.WrappedKey, cancellationToken);
            var headerHash = SHA256.HashData(header.Bytes);
            using var plaintextHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var aes = new AesGcm(dataKey, TagLength);
            long totalPlaintext = 0;
            uint chunkIndex = 0;
            var processedRecord = false;
            var ciphertext = new byte[header.ChunkSize];
            var plaintext = new byte[header.ChunkSize];
            var tag = new byte[TagLength];

            try
            {
                do
                {
                    var plaintextLength = await ReadInt32Async(source, cancellationToken);
                    ValidateRecordLength(plaintextLength, header.ChunkSize, totalPlaintext, header.OriginalLength);
                    await ReadExactlyAsync(source, ciphertext.AsMemory(0, plaintextLength), cancellationToken);
                    await ReadExactlyAsync(source, tag, cancellationToken);

                    var nonce = BuildNonce(header.NonceBase, chunkIndex);
                    var additionalData = BuildAdditionalData(headerHash, chunkIndex, plaintextLength);
                    aes.Decrypt(
                        nonce,
                        ciphertext.AsSpan(0, plaintextLength),
                        tag,
                        plaintext.AsSpan(0, plaintextLength),
                        additionalData);
                    if (plaintextLength > 0) plaintextHash.AppendData(plaintext, 0, plaintextLength);
                    totalPlaintext += plaintextLength;
                    processedRecord = true;
                    chunkIndex = checked(chunkIndex + 1);
                }
                while (totalPlaintext < header.OriginalLength || !processedRecord);

                if (totalPlaintext != header.OriginalLength || source.Position != source.Length)
                    throw new CryptographicException("The encrypted file is incomplete or contains unexpected trailing data.");

                actualHash = plaintextHash.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                    throw new CryptographicException("The encrypted file failed plaintext integrity verification.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ciphertext);
                CryptographicOperations.ZeroMemory(plaintext);
                CryptographicOperations.ZeroMemory(tag);
                CryptographicOperations.ZeroMemory(headerHash);
            }
        }
        catch (EndOfStreamException exception)
        {
            throw new CryptographicException("The encrypted file is incomplete or damaged.", exception);
        }
        finally
        {
            if (actualHash is not null) CryptographicOperations.ZeroMemory(actualHash);
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static Header BuildHeader(
        Guid fileId,
        WrappedFileKey wrappedKey,
        string originalFileName,
        long originalLength,
        int chunkSize,
        byte[] nonceBase)
    {
        var safeName = SanitizeFileName(originalFileName);
        var providerBytes = GetValidatedUtf8(wrappedKey.Provider, MaximumProviderBytes, "key provider");
        var keyIdBytes = GetValidatedUtf8(wrappedKey.KeyId, MaximumKeyIdBytes, "key identifier");
        var wrappedKeyBytes = Convert.FromBase64String(wrappedKey.WrappedKeyBase64);
        var fileNameBytes = GetValidatedUtf8(safeName, MaximumFileNameBytes, "file name");
        if (wrappedKeyBytes.Length is <= 0 or > MaximumWrappedKeyBytes)
            throw new InvalidOperationException("The wrapped file key has an unsupported length.");

        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(fileId.ToByteArray());
            WriteBytes(writer, providerBytes);
            WriteBytes(writer, keyIdBytes);
            WriteBytes(writer, wrappedKeyBytes);
            WriteBytes(writer, fileNameBytes);
            writer.Write(originalLength);
            writer.Write(chunkSize);
            writer.Write(nonceBase);
        }

        CryptographicOperations.ZeroMemory(wrappedKeyBytes);
        return new Header(memory.ToArray(), fileId, wrappedKey, safeName, originalLength, chunkSize, nonceBase);
    }

    private static async Task<Header> ReadHeaderAsync(Stream source, CancellationToken cancellationToken)
    {
        var magic = new byte[Magic.Length];
        await ReadExactlyAsync(source, magic, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(magic, Magic))
            throw new CryptographicException("This is not a supported Company DLP v2 encrypted file.");

        var version = new byte[1];
        await ReadExactlyAsync(source, version, cancellationToken);
        if (version[0] != FormatVersion)
            throw new CryptographicException($"Encrypted file version {version[0]} is not supported.");

        var fileIdBytes = new byte[16];
        await ReadExactlyAsync(source, fileIdBytes, cancellationToken);
        var fileId = new Guid(fileIdBytes);
        var providerBytes = await ReadLengthPrefixedBytesAsync(source, MaximumProviderBytes, cancellationToken);
        var keyIdBytes = await ReadLengthPrefixedBytesAsync(source, MaximumKeyIdBytes, cancellationToken);
        var wrappedKeyBytes = await ReadLengthPrefixedBytesAsync(source, MaximumWrappedKeyBytes, cancellationToken);
        var fileNameBytes = await ReadLengthPrefixedBytesAsync(source, MaximumFileNameBytes, cancellationToken);
        var originalLength = await ReadInt64Async(source, cancellationToken);
        if (originalLength < 0) throw new CryptographicException("The encrypted file contains an invalid original length.");
        var chunkSize = await ReadInt32Async(source, cancellationToken);
        if (chunkSize is < 64 * 1024 or > 16 * 1024 * 1024)
            throw new CryptographicException("The encrypted file contains an unsupported chunk size.");
        var nonceBase = new byte[NonceLength];
        await ReadExactlyAsync(source, nonceBase, cancellationToken);

        var wrappedKey = new WrappedFileKey
        {
            Provider = Encoding.UTF8.GetString(providerBytes),
            KeyId = Encoding.UTF8.GetString(keyIdBytes),
            WrappedKeyBase64 = Convert.ToBase64String(wrappedKeyBytes)
        };
        var originalFileName = Encoding.UTF8.GetString(fileNameBytes);

        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(fileIdBytes);
            WriteBytes(writer, providerBytes);
            WriteBytes(writer, keyIdBytes);
            WriteBytes(writer, wrappedKeyBytes);
            WriteBytes(writer, fileNameBytes);
            writer.Write(originalLength);
            writer.Write(chunkSize);
            writer.Write(nonceBase);
        }

        CryptographicOperations.ZeroMemory(wrappedKeyBytes);
        return new Header(memory.ToArray(), fileId, wrappedKey, originalFileName, originalLength, chunkSize, nonceBase);
    }

    private static void ValidateRecordLength(int plaintextLength, int chunkSize, long totalPlaintext, long originalLength)
    {
        if (plaintextLength < 0 || plaintextLength > chunkSize)
            throw new CryptographicException("The encrypted file contains an invalid chunk length.");
        if (totalPlaintext + plaintextLength > originalLength)
            throw new CryptographicException("The encrypted file contains more data than its authenticated header allows.");
    }

    private static async Task<byte[]> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultChunkSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[DefaultChunkSize];
        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0) break;
                hash.AppendData(buffer, 0, bytesRead);
            }
            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void EncryptChunk(
        AesGcm aes,
        byte[] nonceBase,
        byte[] headerHash,
        uint chunkIndex,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        var nonce = BuildNonce(nonceBase, chunkIndex);
        var additionalData = BuildAdditionalData(headerHash, chunkIndex, plaintext.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, additionalData);
    }

    private static byte[] BuildNonce(byte[] nonceBase, uint chunkIndex)
    {
        var nonce = nonceBase.ToArray();
        var tail = BinaryPrimitives.ReadUInt32BigEndian(nonce.AsSpan(NonceLength - 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NonceLength - 4, 4), tail ^ chunkIndex);
        return nonce;
    }

    private static byte[] BuildAdditionalData(byte[] headerHash, uint chunkIndex, int plaintextLength)
    {
        var additionalData = new byte[headerHash.Length + 8];
        headerHash.CopyTo(additionalData, 0);
        BinaryPrimitives.WriteUInt32BigEndian(additionalData.AsSpan(headerHash.Length, 4), chunkIndex);
        BinaryPrimitives.WriteInt32BigEndian(additionalData.AsSpan(headerHash.Length + 4, 4), plaintextLength);
        return additionalData;
    }

    private static async Task WriteRecordAsync(
        Stream destination,
        int plaintextLength,
        ReadOnlyMemory<byte> ciphertext,
        byte[] tag,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthBuffer, plaintextLength);
        await destination.WriteAsync(lengthBuffer, cancellationToken);
        if (!ciphertext.IsEmpty) await destination.WriteAsync(ciphertext, cancellationToken);
        await destination.WriteAsync(tag, cancellationToken);
    }

    private static async Task<int> ReadChunkAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static async Task<byte[]> ReadLengthPrefixedBytesAsync(Stream source, int maximumLength, CancellationToken cancellationToken)
    {
        var length = await ReadInt32Async(source, cancellationToken);
        if (length is <= 0 || length > maximumLength)
            throw new CryptographicException("The encrypted file contains an invalid header field length.");
        var bytes = new byte[length];
        await ReadExactlyAsync(source, bytes, cancellationToken);
        return bytes;
    }

    private static async Task<int> ReadInt32Async(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[4];
        await ReadExactlyAsync(source, buffer, cancellationToken);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    private static async Task<long> ReadInt64Async(Stream source, CancellationToken cancellationToken)
    {
        var buffer = new byte[8];
        await ReadExactlyAsync(source, buffer, cancellationToken);
        return BinaryPrimitives.ReadInt64LittleEndian(buffer);
    }

    private static async Task ReadExactlyAsync(Stream source, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await source.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            total += read;
        }
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] GetValidatedUtf8(string value, int maximumBytes, string fieldName)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        if (bytes.Length is <= 0 || bytes.Length > maximumBytes)
            throw new InvalidOperationException($"The {fieldName} is empty or too long.");
        return bytes;
    }

    private static string ValidateSourceFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("The selected file does not exist.", fullPath);
        return fullPath;
    }

    private static string SanitizeFileName(string value)
    {
        var fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName)) return "decrypted-file.bin";
        foreach (var invalid in Path.GetInvalidFileNameChars()) fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "decrypted-file.bin" : fileName;
    }

    private static string GetUniquePath(string requestedPath)
    {
        if (!File.Exists(requestedPath) && !Directory.Exists(requestedPath)) return requestedPath;
        var directory = Path.GetDirectoryName(requestedPath)!;
        var fileName = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        for (var index = 1; index <= 9999; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        throw new IOException("Could not create a unique output file name.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private sealed record Header(
        byte[] Bytes,
        Guid FileId,
        WrappedFileKey WrappedKey,
        string OriginalFileName,
        long OriginalLength,
        int ChunkSize,
        byte[] NonceBase);
}
