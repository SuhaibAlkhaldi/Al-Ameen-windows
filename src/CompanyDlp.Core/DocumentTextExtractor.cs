using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace CompanyDlp.Core;

// Port of the Python AI-DLP backend's FileParser.extract_text (services/file_preprocessing/parser.py):
// same supported extensions, same "return empty/best-effort text rather than throw on a single bad
// page/paragraph" behavior for .pdf/.docx. Image extensions (.jpg/.jpeg/.png) route through OCR
// (ocr_extractor.py's Tesseract-based approach) instead of direct text extraction.
public static class DocumentTextExtractor
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".pdf", ".docx", ".jpg", ".jpeg", ".png"
    };

    public static bool IsSupported(string extension) => SupportedExtensions.Contains(extension);

    // Set once at startup (see Program.cs) - a static field rather than a constructor/DI parameter
    // here because this class stays a stateless static utility (LocalAiFileClassificationProvider's
    // only two call sites, IsSupported/ExtractText, must not change shape for this to be additive).
    private static ImageOcrExtractor? _imageOcrExtractor;

    public static void ConfigureImageOcr(ImageOcrExtractor extractor)
        => _imageOcrExtractor = extractor ?? throw new ArgumentNullException(nameof(extractor));

    public static string ExtractText(Stream content, string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".txt" => ExtractTxt(content),
            ".pdf" => ExtractPdf(content),
            ".docx" => ExtractDocx(content),
            ".jpg" or ".jpeg" or ".png" => ExtractImage(content),
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

    private static string ExtractImage(Stream content)
    {
        if (_imageOcrExtractor is null)
            throw new InvalidOperationException(
                "Image OCR was not configured - ConfigureImageOcr must be called during startup before classifying images.");

        return _imageOcrExtractor.ExtractText(content);
    }
}
