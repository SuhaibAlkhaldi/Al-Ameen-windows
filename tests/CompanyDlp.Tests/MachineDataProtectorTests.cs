using System.ComponentModel;
using System.Security.Cryptography;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

// Real Windows DPAPI round-trips (CryptProtectData/CryptUnprotectData, LocalMachine-scoped) - no
// fakes, this is small/fast/side-effect-free (DPAPI keys are derived from machine secrets, nothing is
// written to disk by this class itself).
public sealed class MachineDataProtectorTests
{
    [Fact]
    public void Protect_ThenUnprotect_WithSamePurpose_ReturnsOriginalData()
    {
        var protector = new MachineDataProtector();
        var original = RandomNumberGenerator.GetBytes(64);

        var protectedData = protector.Protect(original, "CompanyDlp.Test.PurposeA");
        var unprotected = protector.Unprotect(protectedData, "CompanyDlp.Test.PurposeA");

        Assert.Equal(original, unprotected);
        Assert.NotEqual(original, protectedData);
    }

    [Fact]
    public void Protect_DefaultPurpose_RoundTrips()
    {
        var protector = new MachineDataProtector();
        var original = RandomNumberGenerator.GetBytes(32);

        var protectedData = protector.Protect(original);
        var unprotected = protector.Unprotect(protectedData);

        Assert.Equal(original, unprotected);
    }

    // The real security property the audit flagged: ciphertext protected under one purpose string
    // must not be unprotectable under a different one. MachineDataProtector derives DPAPI's optional
    // entropy from SHA-256(purpose), so a different purpose is a different entropy value entirely -
    // this is what stops a ciphertext-swap attack (e.g. substituting a trusted-clock-state blob where
    // a file-key blob was expected) from silently "working".
    [Fact]
    public void Unprotect_WithDifferentPurpose_Fails()
    {
        var protector = new MachineDataProtector();
        var original = RandomNumberGenerator.GetBytes(32);
        var protectedData = protector.Protect(original, "CompanyDlp.FileKey.v2.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedData, "CompanyDlp.TrustedClock.v1"));
    }

    // Same property, using the two REAL purpose strings the audit specifically named: file keys
    // (FileKeyProtector) vs. trusted-clock state (TrustedClock). Confirmed cross-purpose failure in
    // both directions, not just one.
    [Fact]
    public void Unprotect_FileKeyCiphertext_UnderTrustedClockPurpose_Fails()
    {
        var protector = new MachineDataProtector();
        var fileKey = RandomNumberGenerator.GetBytes(32);
        var protectedAsFileKey = protector.Protect(fileKey, "CompanyDlp.FileKey.v2.bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedAsFileKey, "CompanyDlp.TrustedClock.v1"));
    }

    [Fact]
    public void Unprotect_TrustedClockCiphertext_UnderFileKeyPurpose_Fails()
    {
        var protector = new MachineDataProtector();
        var clockState = RandomNumberGenerator.GetBytes(48);
        var protectedAsClockState = protector.Protect(clockState, "CompanyDlp.TrustedClock.v1");

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedAsClockState, "CompanyDlp.FileKey.v2.cccccccccccccccccccccccccccccccc"));
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Fails()
    {
        var protector = new MachineDataProtector();
        var original = RandomNumberGenerator.GetBytes(32);
        var protectedData = protector.Protect(original, "CompanyDlp.Test.Tamper");
        protectedData[^1] ^= 0xFF;

        Assert.ThrowsAny<Exception>(() => protector.Unprotect(protectedData, "CompanyDlp.Test.Tamper"));
    }
}
