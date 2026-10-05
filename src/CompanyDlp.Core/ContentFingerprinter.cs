using System.Security.Cryptography;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;

namespace CompanyDlp.Core;

// Fingerprints a file's own content, independent of the classification watermark the agent writes into
// it. Two files with the same text share a fingerprint even when their watermark headers differ (another
// device, another scan time), so a full copy stays detectable across devices. Returns null for formats
// this does not yet read (images), whose byte hash remains the only identity for them.
public static class ContentFingerprinter
{
    // Names ContentWatermarker gives the watermark objects it adds to PowerPoint slides. Kept in sync with
    // ContentWatermarker by hand: a shape carrying one of these names is the watermark, never the file's text.
    private const string PptxWatermarkShapeName = "CompanyDlpWatermark";

    // Lines ContentWatermarker prints as its corner and tile text (PDF, and anywhere else the text appears).
    private static readonly string[] WatermarkLinePrefixes = ["Classification:", "Device:", "Last Scanned:"];

    public static string? TryCompute(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".txt" => HashNormalized(ContentWatermarker.StripTxtWatermarkBlocks(File.ReadAllText(path))),
                ".docx" => TryComputeDocx(path),
                ".pptx" => TryComputePptx(path),
                ".xlsx" => TryComputeWithExtractor(path, ".xlsx"),
                ".pdf" => TryComputePdf(path),
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    // Only the document body is read. The classification watermark lives in the document's header parts
    // (see ContentWatermarker.WatermarkDocx), so the body is the file's own content by construction.
    private static string? TryComputeDocx(string path)
    {
        using var stream = OpenShared(path);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var body = document.MainDocumentPart?.Document?.Body;
        if (body == null) return null;

        var text = string.Join("\n", body.Descendants<Paragraph>().Select(paragraph => paragraph.InnerText));
        return HashNormalized(text);
    }

    // Text of every slide shape except the watermark shape (named PptxWatermarkShapeName).
    private static string? TryComputePptx(string path)
    {
        using var stream = OpenShared(path);
        using var document = PresentationDocument.Open(stream, isEditable: false);

        var presentationPart = document.PresentationPart;
        if (presentationPart == null) return null;

        var builder = new StringBuilder();
        foreach (var slidePart in presentationPart.SlideParts)
        {
            var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
            if (shapeTree == null) continue;

            foreach (var shape in shapeTree.Descendants<Shape>())
            {
                if (shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == PptxWatermarkShapeName) continue;

                foreach (var textElement in shape.Descendants<DocumentFormat.OpenXml.Drawing.Text>())
                {
                    builder.Append(textElement.Text).Append(' ');
                }
                builder.Append('\n');
            }
        }
        return HashNormalized(builder.ToString());
    }

    // Cell values only. The watermark is drawn as a worksheet drawing, not as cell content.
    private static string? TryComputeWithExtractor(string path, string extension)
    {
        using var stream = OpenShared(path);
        return HashNormalized(DocumentTextExtractor.ExtractText(stream, extension));
    }

    // Page text with the watermark's own lines removed. PDF has no object structure for the watermark,
    // so it is recognized by the fixed lines it prints.
    private static string? TryComputePdf(string path)
    {
        using var stream = OpenShared(path);
        var text = DocumentTextExtractor.ExtractText(stream, ".pdf");

        var builder = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (WatermarkLinePrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.Ordinal))) continue;
            builder.Append(line).Append('\n');
        }
        return HashNormalized(builder.ToString());
    }

    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

    private static string HashNormalized(string text)
    {
        var normalized = text.Replace("\r\n", "\n").TrimEnd();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
