using CompanyDlp.Service;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class SelfWrittenContentRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dlp-selfwrite-" + Guid.NewGuid().ToString("N"));

    public SelfWrittenContentRegistryTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    [Fact]
    public void IsSelfWritten_ReturnsTrue_ForTheContentHashRecordedAfterARewrite()
    {
        var path = Path.Combine(_directory, "report.txt");
        File.WriteAllText(path, "watermarked content");
        var registry = new SelfWrittenContentRegistry();

        registry.RecordRewrite(path);

        Assert.True(registry.IsSelfWritten(FileInventoryContentResolver.ComputeHash(path)!));
    }

    [Fact]
    public void IsSelfWritten_StillMatches_AfterTheFileIsRenamed()
    {
        var path = Path.Combine(_directory, "report.txt");
        File.WriteAllText(path, "watermarked content");
        var registry = new SelfWrittenContentRegistry();
        registry.RecordRewrite(path);

        var renamedPath = Path.Combine(_directory, "[Secret] report.txt");
        File.Move(path, renamedPath);

        Assert.True(registry.IsSelfWritten(FileInventoryContentResolver.ComputeHash(renamedPath)!));
    }

    [Fact]
    public void IsSelfWritten_ReturnsFalse_WhenTheContentIsNotTheRewrittenContent()
    {
        var path = Path.Combine(_directory, "report.txt");
        File.WriteAllText(path, "watermarked content");
        var registry = new SelfWrittenContentRegistry();
        registry.RecordRewrite(path);

        File.WriteAllText(path, "user edited this afterwards");

        Assert.False(registry.IsSelfWritten(FileInventoryContentResolver.ComputeHash(path)!));
    }

    [Fact]
    public void IsSelfWritten_ReturnsFalse_ForContentNeverRewrittenByTheAgent()
    {
        var path = Path.Combine(_directory, "untouched.txt");
        File.WriteAllText(path, "plain content");
        var registry = new SelfWrittenContentRegistry();

        Assert.False(registry.IsSelfWritten(FileInventoryContentResolver.ComputeHash(path)!));
    }
}
