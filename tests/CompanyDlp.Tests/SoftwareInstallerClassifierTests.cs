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

    // Regression test for the false positive confirmed live 2026-08-17: ContainsPackageArgument used
    // to do a raw substring Contains(".appx") over the whole command line, so backgroundTaskHost.exe's
    // real, benign "-ServerName:App.AppX<guid>.mca" argument (the "AppX" here is just part of an
    // auto-generated package-family GUID, not a real .appx file) matched every time and produced 62+
    // false "Software Install" audit events on a live production device for a single Windows-internal
    // process, with zero genuine installer signal. The fix tokenizes on whitespace and checks EndsWith
    // per token, since a real installer argument always has the extension as the actual suffix of its
    // path/filename token.
    [Fact]
    public void BackgroundTaskHost_WithAppxSubstringInGuid_IsNotDetectedAsInstaller()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "backgroundTaskHost.exe",
                @"C:\WINDOWS\system32\backgroundTaskHost.exe",
                @"""C:\WINDOWS\system32\backgroundTaskHost.exe"" -ServerName:App.AppX3yypww7qrft4zqhh57xaatcrnp803bj7.mca",
                "CN=Microsoft Windows",
                1),
            Policy());

        Assert.False(result.IsInstaller);
        Assert.NotEqual("AppPackageArgument", result.DetectionReason);
    }

    // Same fix, opposite direction: a genuine .appx package argument - where the extension is the real
    // suffix of the path token - must still be detected. Guards against an overcorrection that stops
    // matching real installer arguments entirely.
    [Fact]
    public void GenericProcess_WithRealAppxPathArgument_IsDetected()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "AppInstallerCLI.exe",
                @"C:\Users\Employee\Downloads\AppInstallerCLI.exe",
                @"AppInstallerCLI.exe --add-package ""C:\Users\Employee\Downloads\SomeApp.appx""",
                "Unknown",
                2),
            Policy());

        Assert.True(result.IsInstaller);
        Assert.Equal("AppPackageArgument", result.DetectionReason);
    }

    // Same fix applied to the MSI branch (both extension lists go through the same
    // ContainsPackageArgument helper) - a real .msi path argument to a non-msiexec process must still
    // be detected via AppPackageArgument/MsiPackageArgument's tokenized EndsWith check.
    [Fact]
    public void GenericProcess_WithRealMsiPathArgument_IsDetected()
    {
        var result = SoftwareInstallerClassifier.Classify(
            new SoftwareProcessDescriptor(
                "launcher.exe",
                @"C:\Users\Employee\Downloads\launcher.exe",
                @"launcher.exe /silent ""C:\Users\Employee\Downloads\product.msi""",
                "Unknown",
                2),
            Policy());

        Assert.True(result.IsInstaller);
        Assert.Equal("MsiPackageArgument", result.DetectionReason);
    }
}
