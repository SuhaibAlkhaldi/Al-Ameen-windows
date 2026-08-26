using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace CompanyDlp.Core;

// Port of the Python AI-DLP backend's FileParser.extract_text (services/file_preprocessing/parser.py):
// same supported extensions, same "return empty/best-effort text rather than throw on a single bad
// page/paragraph" behavior for .pdf/.docx. Image extensions (.jpg/.jpeg/.png) route through OCR
// (ocr_extractor.py's Tesseract-based approach) instead of direct text extraction.
//
// .pptx/.xlsx added 2026-08-26 alongside ContentWatermarker's matching support for those formats -
// confirmed live that ContentWatermarker already had (unreachable) PPTX watermarking code, because
// this class - the single gate FileInventoryScanner checks before a file can ever be classified at
// all - never listed .pptx as supported, so a PowerPoint file was always marked Unsupported and
// never reached classification, let alone watermarking. Excel workbooks were never wired up at all.
public static class DocumentTextExtractor
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".pdf", ".docx", ".pptx", ".xlsx", ".jpg", ".jpeg", ".png"
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
            ".pptx" => ExtractPptx(content),
            ".xlsx" => ExtractXlsx(content),
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

    private static string ExtractPptx(Stream content)
    {
        using var document = DocumentFormat.OpenXml.Packaging.PresentationDocument.Open(content, isEditable: false);
        var presentationPart = document.PresentationPart;
        if (presentationPart is null) return string.Empty;

        var textBuilder = new System.Text.StringBuilder();
        foreach (var slidePart in presentationPart.SlideParts)
        {
            var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
            if (shapeTree is null) continue;

            // a:t (DrawingML text run) is the one element every piece of visible slide text funnels
            // through regardless of which shape/placeholder/table cell it lives in - descendants(),
            // not a per-shape-type walk, so nothing (title, body, a table cell, a text box) is missed.
            foreach (var textElement in shapeTree.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
            {
                if (!string.IsNullOrEmpty(textElement.Text))
                {
                    textBuilder.Append(textElement.Text).Append(' ');
                }
            }
            textBuilder.Append('\n');
        }
        return textBuilder.ToString();
    }

    private static string ExtractXlsx(Stream content)
    {
        using var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(content, isEditable: false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart is null) return string.Empty;

        // Shared strings: most text cells in a real workbook store an INDEX into this table rather
        // than their own literal text, to avoid repeating common strings across thousands of cells -
        // must be resolved up front or cell text reads back as a meaningless integer.
        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        var textBuilder = new System.Text.StringBuilder();
        foreach (var worksheetPart in workbookPart.WorksheetParts)
        {
            foreach (var cell in worksheetPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>())
            {
                var text = ReadCellText(cell, sharedStrings);
                if (!string.IsNullOrEmpty(text))
                {
                    textBuilder.Append(text).Append(' ');
                }
            }
            textBuilder.Append('\n');
        }
        return textBuilder.ToString();
    }

    private static string? ReadCellText(
        DocumentFormat.OpenXml.Spreadsheet.Cell cell,
        DocumentFormat.OpenXml.Spreadsheet.SharedStringTable? sharedStrings)
    {
        var raw = cell.CellValue?.InnerText;

        if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString)
        {
            if (raw is null || sharedStrings is null || !int.TryParse(raw, out var index)) return null;
            var items = sharedStrings.Elements<DocumentFormat.OpenXml.Spreadsheet.SharedStringItem>().ToList();
            return index >= 0 && index < items.Count ? items[index].InnerText : null;
        }

        if (cell.DataType?.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText;
        }

        // Numbers/dates/booleans/formula results all land here as their raw cached text - good
        // enough for keyword/PII detection over a workbook's content, which is this method's only
        // consumer (LocalAiFileClassificationProvider), not a spreadsheet UI.
        return raw;
    }

    private static string ExtractImage(Stream content)
    {
        if (_imageOcrExtractor is null)
            throw new InvalidOperationException(
                "Image OCR was not configured - ConfigureImageOcr must be called during startup before classifying images.");

        return _imageOcrExtractor.ExtractText(content);
    }
}
