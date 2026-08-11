using System.Security.Cryptography;
using CompanyDlp.Contracts;
using CompanyDlp.Core;
using CompanyDlp.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CompanyDlp.Tests;

// Tests the real LocalMachineDpapi provider path (the default - DlpPolicy.FileProtection.KeyProvider
// defaults to "LocalMachineDpapi", see CreateDefault() below) end to end through the real
// MachineDataProtector/DPAPI, not a fake IFileKeyProtector (that's what FileProtectionEngineTests
// uses to isolate FileProtectionEngine itself - this class tests FileKeyProtector directly instead).
// identityProvider/backendApiClient are passed null: confirmed by reading WrapAsync/UnwrapAsync that
// neither is touched anywhere on the LocalMachineDpapi branch (only BackendKms uses them).
public sealed class FileKeyProtectorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CompanyDlpFileKeyProtectorTests", Guid.NewGuid().ToString("N"));
    private readonly string? _oldPolicyPath = Environment.GetEnvironmentVariable("COMPANY_DLP_POLICY_PATH");

    private FileKeyProtector CreateProtector()
    {
        Directory.CreateDirectory(_directory);
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", Path.Combine(_directory, "policy.json"));
        var policyStore = new PolicyStore(new MachineDataProtector(), NullLogger<PolicyStore>.Instance);
        Assert.Equal("LocalMachineDpapi", policyStore.Get().FileProtection.KeyProvider);

        return new FileKeyProtector(policyStore, identityProvider: null!, new MachineDataProtector(), backendApiClient: null!);
    }

    [Fact]
    public async Task WrapAsync_ThenUnwrapAsync_ReturnsOriginalKey()
    {
        var protector = CreateProtector();
        var fileId = Guid.NewGuid();
        var plainKey = RandomNumberGenerator.GetBytes(32);

        var wrapped = await protector.WrapAsync(fileId, plainKey, CancellationToken.None);
        Assert.Equal("LocalMachineDpapi", wrapped.Provider);

        var unwrapped = await protector.UnwrapAsync(fileId, wrapped, CancellationToken.None);

        Assert.Equal(plainKey, unwrapped);
    }

    [Fact]
    public async Task WrapAsync_RejectsNonstandardKeyLength()
    {
        var protector = CreateProtector();

        await Assert.ThrowsAsync<CryptographicException>(
            () => protector.WrapAsync(Guid.NewGuid(), RandomNumberGenerator.GetBytes(16), CancellationToken.None));
    }

    [Fact]
    public async Task UnwrapAsync_TamperedCiphertext_ThrowsPredictably()
    {
        var protector = CreateProtector();
        var fileId = Guid.NewGuid();
        var wrapped = await protector.WrapAsync(fileId, RandomNumberGenerator.GetBytes(32), CancellationToken.None);

        var bytes = Convert.FromBase64String(wrapped.WrappedKeyBase64);
        bytes[^1] ^= 0xFF;
        wrapped.WrappedKeyBase64 = Convert.ToBase64String(bytes);

        await Assert.ThrowsAnyAsync<Exception>(() => protector.UnwrapAsync(fileId, wrapped, CancellationToken.None));
    }

    // The real security property under test: FileKeyProtector derives a per-file DPAPI purpose from
    // the fileId (Purpose(fileId) = "CompanyDlp.FileKey.v2.<fileId:N>"), so a wrapped key can only be
    // unwrapped against the SAME fileId it was wrapped for - a key wrapped for one file's record can't
    // be substituted to unwrap under a different file's identity, even with a byte-for-byte identical
    // ciphertext.
    [Fact]
    public async Task UnwrapAsync_WithMismatchedFileId_Fails()
    {
        var protector = CreateProtector();
        var wrapped = await protector.WrapAsync(Guid.NewGuid(), RandomNumberGenerator.GetBytes(32), CancellationToken.None);

        var differentFileId = Guid.NewGuid();
        await Assert.ThrowsAnyAsync<Exception>(() => protector.UnwrapAsync(differentFileId, wrapped, CancellationToken.None));
    }

    [Fact]
    public async Task UnwrapAsync_UnsupportedProvider_Throws()
    {
        var protector = CreateProtector();
        var wrapped = new WrappedFileKey { Provider = "SomeOtherProvider", KeyId = "x", WrappedKeyBase64 = "" };

        await Assert.ThrowsAsync<CryptographicException>(
            () => protector.UnwrapAsync(Guid.NewGuid(), wrapped, CancellationToken.None));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", _oldPolicyPath);
        try { Directory.Delete(_directory, true); } catch { }
    }
}
