using CompanyDlp.Core;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class ContentFingerprinterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dlp-fingerprint-" + Guid.NewGuid().ToString("N"));

    public ContentFingerprinterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private const string Body = "Quarterly plan: revenue targets and vendor list.\nLine two.";

    [Fact]
    public void SameText_WithDifferentWatermarkHeaders_ProducesSameFingerprint()
    {
        var deviceA = Path.Combine(_directory, "a.txt");
        var deviceB = Path.Combine(_directory, "b.txt");
        File.WriteAllText(deviceA, "Classification: Secret\nDevice: PC-ONE\nLast Scanned: 2026-10-01 09:00\n\n" + Body);
        File.WriteAllText(deviceB, "Classification: Secret\nDevice: PC-TWO\nLast Scanned: 2026-10-05 17:30\n\n" + Body);

        Assert.Equal(ContentFingerprinter.TryCompute(deviceA), ContentFingerprinter.TryCompute(deviceB));
    }

    [Fact]
    public void WatermarkedAndPlainCopies_OfTheSameText_ProduceSameFingerprint()
    {
        var plain = Path.Combine(_directory, "plain.txt");
        var watermarked = Path.Combine(_directory, "watermarked.txt");
        File.WriteAllText(plain, Body);
        File.WriteAllText(watermarked, "Classification: Public\nDevice: PC-ONE\nLast Scanned: 2026-10-05 17:30\n\n" + Body);

        Assert.Equal(ContentFingerprinter.TryCompute(plain), ContentFingerprinter.TryCompute(watermarked));
    }

    [Fact]
    public void DifferentText_ProducesDifferentFingerprint()
    {
        var first = Path.Combine(_directory, "first.txt");
        var second = Path.Combine(_directory, "second.txt");
        File.WriteAllText(first, Body);
        File.WriteAllText(second, Body + " An extra sentence.");

        Assert.NotEqual(ContentFingerprinter.TryCompute(first), ContentFingerprinter.TryCompute(second));
    }

    [Fact]
    public void UnsupportedFormat_ReturnsNull()
    {
        var path = Path.Combine(_directory, "report.pdf");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        Assert.Null(ContentFingerprinter.TryCompute(path));
    }
}
