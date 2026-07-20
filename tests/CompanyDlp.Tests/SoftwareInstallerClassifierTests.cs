using Xunit;
using CompanyDlp.Contracts;
using CompanyDlp.Core;

namespace CompanyDlp.Tests;

public sealed class SoftwareInstallerClassifierTests
{
    private static SoftwarePolicy Policy() => new()
    {
        Enabled = true,
        EnforcementMode = "AuditOnly",
        BlockMsi = true,
        BlockMsixAppx = true,
        BlockKnownInstallers = true
    };

    [Fact]
    public void InstallAgentUserBroker_IsNotAUserInstallationAttempt()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "InstallAgentUserBroker.exe",
                @"C:\Windows\System32\InstallAgentUserBroker.exe",
                "",
                "CN=Microsoft Windows",
                1),
            Policy());

        Assert.False(result.IsInstaller);
        Assert.Equal("MicrosoftWindowsSystemProcess", result.DetectionReason);
    }

    [Fact]
    public void TrustedInstaller_InSessionZero_IsNotAUserInstallationAttempt()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "TrustedInstaller.exe",
                @"C:\Windows\servicing\TrustedInstaller.exe",
                "",
                "CN=Microsoft Windows",
                0),
            Policy());

        Assert.False(result.IsInstaller);
        Assert.Equal("NonInteractiveSession", result.DetectionReason);
    }

    [Fact]
    public void ChromeSetup_InInteractiveSession_IsDetected()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "ChromeSetup.exe",
                @"C:\Users\Employee\Downloads\ChromeSetup.exe",
                @"C:\Users\Employee\Downloads\ChromeSetup.exe",
                "Google LLC",
                2),
            Policy());

        Assert.True(result.IsInstaller);
        Assert.Equal("InstallerNamePattern", result.DetectionReason);
    }

    [Fact]
    public void MsiExec_InInteractiveSession_IsDetected()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "msiexec.exe",
                @"C:\Windows\System32\msiexec.exe",
                @"msiexec.exe /i C:\Users\Employee\Downloads\product.msi",
                "CN=Microsoft Windows",
                2),
            Policy());

        Assert.True(result.IsInstaller);
        Assert.Equal("MsiExec", result.DetectionReason);
    }

    [Fact]
    public void OrdinaryDotnetProcess_IsNotDetected()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "dotnet.exe",
                @"C:\Program Files\dotnet\dotnet.exe",
                @"dotnet CompanyDlp.Service.dll",
                "Microsoft Corporation",
                2),
            Policy());

        Assert.False(result.IsInstaller);
        Assert.Equal("NoInstallerSignal", result.DetectionReason);
    }
}
