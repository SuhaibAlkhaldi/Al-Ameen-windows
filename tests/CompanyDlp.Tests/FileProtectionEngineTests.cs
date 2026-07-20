using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class FileProtectionEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CompanyDlpV2Tests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldPolicyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");

    [Fact]
    public async Task V2Encrypt_VerifiesDeletesPlaintext_AndDecryptsAuthenticatedContent()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "financial-report.bin");
        var original = RandomNumberGenerator.GetBytes(2_100_123);
        await File.WriteAllBytesAsync(source, original);
        var engine = CreateEngine();

        var encrypted = await engine.EncryptAndDeleteOriginalAsync(source);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(encrypted.OutputPath));
        Assert.EndsWith(".dlpenc", encrypted.OutputPath, StringComparison.OrdinalIgnoreCase);

        var decrypted = await engine.DecryptAsync(encrypted.OutputPath);
        Assert.True(File.Exists(decrypted.OutputPath));
        Assert.Equal(original, await File.ReadAllBytesAsync(decrypted.OutputPath));
    }

    [Fact]
    public async Task V2Decrypt_RejectsTamperedCiphertext()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "confidential.txt");
        await File.WriteAllTextAsync(source, "confidential test content");
        var engine = CreateEngine();
        var encrypted = await engine.EncryptAndDeleteOriginalAsync(source);
        var bytes = await File.ReadAllBytesAsync(encrypted.OutputPath);
        bytes[^1] ^= 0x55;
        await File.WriteAllBytesAsync(encrypted.OutputPath, bytes);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => engine.DecryptAsync(encrypted.OutputPath));
    }

    private FileProtectionEngine CreateEngine()
    {
        Directory.CreateDirectory(_directory);
        var policyPath = Path.Combine(_directory, "policy.json");
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", policyPath);
        var store = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        store.Save(new DlpPolicy
        {
            Runtime = new RuntimePolicy { Mode = "Development" },
            FileProtection = new FileProtectionPolicy
            {
                Enabled = true,
                DeletePlaintextAfterVerifiedEncryption = true,
                KeepEncryptedFileAfterDecryption = true,
                MaximumFileSizeBytes = 100 * 1024 * 1024
            }
        });
        return new FileProtectionEngine(store, new InMemoryKeyProtector());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", _oldPolicyPath);
        try { Directory.Delete(_directory, true); } catch { }
    }

    private sealed class InMemoryKeyProtector : IFileKeyProtector
    {
        private readonly Dictionary<Guid, byte[]> _keys = [];

        public Task<WrappedFileKey> WrapAsync(Guid fileId, byte[] plainKey, CancellationToken cancellationToken)
        {
            _keys[fileId] = plainKey.ToArray();
            return Task.FromResult(new WrappedFileKey
            {
                Provider = "Test",
                KeyId = fileId.ToString("N"),
                WrappedKeyBase64 = Convert.ToBase64String(SHA256.HashData(plainKey))
            });
        }

        public Task<byte[]> UnwrapAsync(Guid fileId, WrappedFileKey wrappedKey, CancellationToken cancellationToken) =>
            Task.FromResult(_keys[fileId].ToArray());
    }
}
