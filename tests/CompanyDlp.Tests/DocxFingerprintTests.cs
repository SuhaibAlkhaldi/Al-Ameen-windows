using CompanyDlp.Core;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace CompanyDlp.Tests;

public sealed class DocxFingerprintTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dlp-docx-" + Guid.NewGuid().ToString("N"));

    public DocxFingerprintTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private static string WriteDocx(string path, string bodyText, string headerText)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body(new Paragraph(new Run(new Text(bodyText)))));

        var headerPart = mainPart.AddNewPart<HeaderPart>();
        headerPart.Header = new Header(new Paragraph(new Run(new Text(headerText))));
        var headerRelId = mainPart.GetIdOfPart(headerPart);
        mainPart.Document.Body!.Append(new SectionProperties(new HeaderReference { Type = HeaderFooterValues.Default, Id = headerRelId }));
        mainPart.Document.Save();
        return path;
    }

    [Fact]
    public void SameBody_WithDifferentHeaders_ProducesSameFingerprint()
    {
        var first = WriteDocx(Path.Combine(_directory, "a.docx"), "Revenue targets for the north region.", "Classification: Secret / Device: PC-ONE");
        var second = WriteDocx(Path.Combine(_directory, "b.docx"), "Revenue targets for the north region.", "Classification: Secret / Device: PC-TWO");

        Assert.Equal(ContentFingerprinter.TryCompute(first), ContentFingerprinter.TryCompute(second));
        Assert.NotNull(ContentFingerprinter.TryCompute(first));
    }

    [Fact]
    public void DifferentBody_ProducesDifferentFingerprint()
    {
        var first = WriteDocx(Path.Combine(_directory, "a.docx"), "Revenue targets for the north region.", "header");
        var second = WriteDocx(Path.Combine(_directory, "b.docx"), "Revenue targets for the south region.", "header");

        Assert.NotEqual(ContentFingerprinter.TryCompute(first), ContentFingerprinter.TryCompute(second));
    }
}
