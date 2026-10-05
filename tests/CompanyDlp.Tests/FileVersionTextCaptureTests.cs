using CompanyDlp.Contracts;
using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class FileVersionTextCaptureTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dlp-text-capture-" + Guid.NewGuid().ToString("N"));

    public FileVersionTextCaptureTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(ClassificationTiers.Internal)]
    [InlineData(ClassificationTiers.Secret)]
    [InlineData(ClassificationTiers.VerySecret)]
    public void SensitiveTier_KeepsText(string tier)
    {
        Assert.Equal("plan", FileVersionTextCapture.ForEnvelope("plan", tier, isProtected: false));
    }

    [Theory]
    [InlineData(ClassificationTiers.Public)]
    [InlineData("Sensitive")]
    public void NonSensitiveOrUnknownTier_DropsText(string tier)
    {
        Assert.Null(FileVersionTextCapture.ForEnvelope("plan", tier, isProtected: false));
    }

    [Fact]
    public void MissingTier_DropsText()
    {
        Assert.Null(FileVersionTextCapture.ForEnvelope("plan", null, isProtected: false));
    }

    [Fact]
    public void ProtectedFile_NeverCarriesText()
    {
        Assert.Null(FileVersionTextCapture.ForEnvelope("ciphertext-derived", ClassificationTiers.VerySecret, isProtected: true));
    }

    [Fact]
    public void UnreadableFile_HasNoText()
    {
        Assert.Null(FileVersionTextCapture.ForEnvelope(null, ClassificationTiers.Secret, isProtected: false));
    }

    [Fact]
    public void TextOverCaptureLimit_IsDropped()
    {
        var oversized = new string('a', FileVersionTextCapture.MaxCapturedTextChars + 1);

        Assert.Null(FileVersionTextCapture.ForEnvelope(oversized, ClassificationTiers.Secret, isProtected: false));
    }

    [Fact]
    public void TextAtCaptureLimit_IsKept()
    {
        var atLimit = new string('a', FileVersionTextCapture.MaxCapturedTextChars);

        Assert.Equal(atLimit, FileVersionTextCapture.ForEnvelope(atLimit, ClassificationTiers.Secret, isProtected: false));
    }

    [Fact]
    public void ExtractedText_ExcludesWatermark_AndHashesToTheSentFingerprint()
    {
        var path = Path.Combine(_directory, "secret.txt");
        File.WriteAllText(path, "Classification: Secret\nDevice: PC-ONE\nLast Scanned: 2026-10-05 17:30\n\nRevenue targets.\r\nLine two.\r\n");

        var normalized = ContentFingerprinter.TryExtractNormalizedText(path);

        Assert.NotNull(normalized);
        Assert.DoesNotContain("Classification:", normalized);
        Assert.Equal("Revenue targets.\nLine two.", normalized.TrimStart('\n'));
        Assert.Equal(ContentFingerprinter.TryCompute(path), ContentFingerprinter.HashNormalizedText(normalized));
    }
}
