using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace CompanyDlp.Core;

// Port of the Python AI-DLP backend's FileParser.extract_text (services/file_preprocessing/parser.py):
// same three supported extensions, same "return empty/best-effort text rather than throw on a single
// bad page/paragraph" behavior for .pdf/.docx.
public static class DocumentTextExtractor
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".pdf", ".docx"
    };

    public static bool IsSupported(string extension) => SupportedExtensions.Contains(extension);

    public static string ExtractText(Stream content, string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => ExtractTxt(content),
            ".pdf" => ExtractPdf(content),
            ".docx" => ExtractDocx(content),
            _ => throw new NotSupportedException($"Unsupported file format for extraction: {extension}")
        };
    }

    private static string ExtractTxt(Stream content)
    {
        using var reader = new StreamReader(content, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static string ExtractPdf(Stream content)
    {
        using var document = PdfDocument.Open(content);
        var textBuilder = new System.Text.StringBuilder();
        foreach (var page in document.GetPages())
        {
            var pageText = page.Text;
            if (!string.IsNullOrEmpty(pageText))
            {
                textBuilder.Append(pageText).Append('\n');
            }
        }
        return textBuilder.ToString();
    }

    private static string ExtractDocx(Stream content)
    {
        using var document = WordprocessingDocument.Open(content, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var paragraphs = body.Elements<Paragraph>().Select(p => p.InnerText);
        return string.Join('\n', paragraphs);
    }
}
