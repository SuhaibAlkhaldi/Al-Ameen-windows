using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;
using P = DocumentFormat.OpenXml.Presentation;
using PigPdfDocument = UglyToad.PdfPig.PdfDocument;

namespace CompanyDlp.Core;

// Stamps a file's classification directly into its content, for the file types where that's
// actually safe (see FileInventoryScanner, which calls this once a file has a confirmed
// Up-to-Date classification - same trigger as FilenameClassificationTagger, a separate concern).
// Deliberately NOT attempted for CSV/JSON/XML-style structured formats (would corrupt them for any
// program that parses them) or executables/archives/encrypted files (real risk of breaking
// signatures or the file's function entirely) - see DocumentTextExtractor.SupportedExtensions vs.
// this class's SupportedExtensions for the (larger) set this class covers, since watermarking a
// file doesn't require being able to read its text content the way classification does.
//
// Visual design: a small, clearly-legible, non-rotated info block (Classification/Status/Last
// Scanned) placed in a page/image corner - NOT the large rotated semi-transparent diagonal stamp
// this class started with. Changed after live feedback: the diagonal stamp looked unprofessional
// and, worse, sat directly on top of the document's own content instead of beside it.
//
// Every per-format method writes to a temp file first, then File.Move(overwrite) - same
// write-safety convention used throughout this codebase (PolicyStore, FileClassificationCache,
// etc.) - so a crash mid-write can never leave a half-written file in the original's place.
public static class ContentWatermarker
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".pdf", ".docx", ".pptx", ".jpg", ".jpeg", ".png"
    };

    public static bool IsSupported(string extension) => SupportedExtensions.Contains(extension);

    private static readonly Dictionary<string, string> LabelsByTier = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = "PUBLIC",
        // Wire value stays "Internal" (ClassificationTiers.Internal constant unchanged) - only the
        // stamped watermark text changed, per explicit request, to "RESTRICTED".
        [ClassificationTiers.Internal] = "RESTRICTED",
        [ClassificationTiers.Secret] = "SECRET",
        [ClassificationTiers.VerySecret] = "VERY SECRET",
    };

    // Never throws - a watermarking failure for one file must never take down the background
    // scan loop, same "soft failure, log and move on" contract as everything else FileInventoryScanner
    // calls. Returns true only if the file's bytes were actually rewritten (so the caller knows to
    // re-stat the file's LastWriteTimeUtc before recording it as "seen").
    //
    // lastScannedUtc is accepted from the caller rather than read here: FileInventoryScanner is
    // about to write this exact same timestamp into FileClassificationStatusStore, and the two
    // must agree - the file's own displayed "Last Scanned" text must never say something different
    // than the "Last Scanned" the Properties tab and Explorer column show for the same file.
    //
    // Status is not a parameter: this is only ever called from the branches that just determined a
    // file's status IS "Up to Date" (see FileInventoryScanner), so the displayed value is always
    // that fixed string - a parameter that can only ever hold one value would be pure ceremony.
    public static bool ApplyWatermark(string filePath, string classificationTier, DateTimeOffset lastScannedUtc, ILogger logger)
    {
        if (!LabelsByTier.TryGetValue(classificationTier, out var label)) return false;
        var extension = Path.GetExtension(filePath);
        if (!IsSupported(extension)) return false;

        var lines = BuildInfoLines(label, lastScannedUtc);

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".txt": WatermarkTxt(filePath, lines); return true;
                case ".pdf": WatermarkPdf(filePath, lines); return true;
                case ".docx": WatermarkDocx(filePath, lines); return true;
                case ".pptx": WatermarkPptx(filePath, lines); return true;
                case ".jpg" or ".jpeg" or ".png": WatermarkImage(filePath, lines); return true;
                default: return false;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not apply a content watermark to {Path}; leaving its content unchanged.", filePath);
            return false;
        }
    }

    private static string[] BuildInfoLines(string label, DateTimeOffset lastScannedUtc) =>
    [
        $"Classification: {label}",
        "Status: Up to Date",
        $"Last Scanned: {lastScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
    ];

    // === TXT: a plain text block at the top of the file - the only option for a format with no
    // visual/rendering concept at all (there is no "corner" in plain text). Just the same three
    // lines used everywhere else (Classification/Status/Last Scanned), no extra label - detected
    // by that fixed three-line shape itself so reclassification updates in place instead of
    // stacking on every scan. IgnoreCase: confirmed live that without it, a user hand-editing a
    // value in place (e.g. typing "public" over "INTERNAL") produces text this regex no longer
    // recognizes as "our" block, and the next scan prepends a second one instead of replacing it.
    private static readonly Regex TxtMarkerBlock = new(
        @"\AClassification: .*\r?\nStatus: .*\r?\nLast Scanned: .*\r?\n\r?\n",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void WatermarkTxt(string filePath, string[] lines)
    {
        var original = File.ReadAllText(filePath);

        // Loop, not a single Replace(...) - cleans up any blocks that already stacked up from
        // before this format existed or from a transient bug, converging back to exactly one
        // regardless of how many accumulated.
        var stripped = original;
        while (TxtMarkerBlock.IsMatch(stripped))
        {
            stripped = TxtMarkerBlock.Replace(stripped, string.Empty, 1);
        }

        var block = string.Join(Environment.NewLine, lines) + Environment.NewLine + Environment.NewLine;
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, block + stripped);
        File.Move(temporary, filePath, true);
    }

    // === PDF: a small, right-aligned, non-rotated text block in the top-right corner of every
    // page via PdfSharp - PdfPig (this project's other PDF dependency, used by
    // DocumentTextExtractor) is read-only and cannot write, hence the separate library.
    // Wide enough for the longest possible line ("Classification: VERY SECRET") at 9pt bold Arial,
    // plus margin. Fixed regardless of the file's current tier - unlike TXT/DOCX/PPTX, PdfSharp
    // gives no easy way to locate and erase a specific string already baked into a page's content
    // stream, so instead every call paints an opaque panel of this SAME size first. That keeps
    // repeated scans visually idempotent (each pass fully covers whatever an earlier pass drew in
    // the same spot) even if the classification tier - and so the text length - changed in between.
    private const double PdfWatermarkPanelWidthPoints = 190;

    private static void WatermarkPdf(string filePath, string[] lines)
    {
        // The scanner calls ApplyWatermark on every scan tick, not just when the file actually
        // changed (see FileInventoryScanner's cache-hit branch). Unlike TXT/DOCX/PPTX, PdfSharp
        // has no way to overwrite an already-embedded watermark in place - each save just appends
        // more content-stream drawing operators - so without this check an untouched PDF would
        // accumulate one more (invisible, since the opaque panel covers it, but still present and
        // extractable) copy of the watermark text on every single tick, forever. Skipping the
        // rewrite entirely when the current watermark already matches keeps this file untouched
        // the vast majority of the time, only re-stamping when something actually changed.
        if (AlreadyHasCurrentPdfWatermark(filePath, lines)) return;

        var temporary = filePath + ".tmp";
        using (var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify))
        {
            var font = new XFont("Arial", 9, XFontStyleEx.Bold);
            var brush = new XSolidBrush(XColor.FromArgb(230, 130, 20, 20));
            var backgroundBrush = new XSolidBrush(XColor.FromArgb(255, 255, 255, 255));
            var format = new XStringFormat { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Near };

            foreach (var page in document.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page);
                const double margin = 18;
                const double lineHeight = 12;

                gfx.DrawRectangle(backgroundBrush,
                    page.Width.Point - PdfWatermarkPanelWidthPoints, 0,
                    PdfWatermarkPanelWidthPoints, margin + lines.Length * lineHeight);

                var y = margin;
                foreach (var line in lines)
                {
                    gfx.DrawString(line, font, brush, new XRect(0, y, page.Width.Point - margin, lineHeight), format);
                    y += lineHeight;
                }
            }

            document.Save(temporary);
        }
        File.Move(temporary, filePath, true);
    }

    // Reads the PDF's extracted text (PdfPig - a separate, read-only library from PdfSharp) and
    // checks whether the exact watermark block we're about to write is already the last thing on
    // every page. Text extraction follows content-stream draw order here, and the watermark is
    // always the last thing drawn onto each page, so this is a reliable "already up to date" check
    // without needing to parse/diff the actual page content stream.
    private static bool AlreadyHasCurrentPdfWatermark(string filePath, string[] lines)
    {
        var expected = string.Concat(lines);
        try
        {
            using var document = PigPdfDocument.Open(filePath);
            return document.GetPages().All(page => page.Text.EndsWith(expected, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    // === DOCX: three small right-aligned lines in the document's own header (so they repeat on
    // every page, in the margin area, never overlapping the body content) - plain Word
    // paragraphs/runs, no VML autoshape needed once the design stopped being a rotated stamp.
    // Detects a previously-added block via a hidden ("vanish") marker run so reclassification
    // replaces the text in place.
    private static void WatermarkDocx(string filePath, string[] lines)
    {
        var temporary = filePath + ".tmp";
        File.Copy(filePath, temporary, true);
        try
        {
            using (var document = WordprocessingDocument.Open(temporary, true))
            {
                var mainPart = document.MainDocumentPart;
                var body = mainPart?.Document?.Body;
                if (mainPart is null || body is null)
                    throw new InvalidOperationException("Not a valid Word document body.");

                var existingHeaderPart = mainPart.HeaderParts.FirstOrDefault(HeaderContainsOurMarker);
                var headerPart = existingHeaderPart ?? mainPart.AddNewPart<HeaderPart>();

                using (var writer = new StreamWriter(headerPart.GetStream(FileMode.Create, FileAccess.Write)))
                {
                    writer.Write(BuildDocxWatermarkHeaderXml(lines));
                }

                if (existingHeaderPart is null)
                {
                    var relationshipId = mainPart.GetIdOfPart(headerPart);
                    var sectionProperties = body.Elements<SectionProperties>().ToList();
                    if (sectionProperties.Count == 0)
                    {
                        var newSectionProperties = new SectionProperties();
                        body.Append(newSectionProperties);
                        sectionProperties.Add(newSectionProperties);
                    }

                    foreach (var sectPr in sectionProperties)
                    {
                        sectPr.RemoveAllChildren<HeaderReference>();
                        sectPr.PrependChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = relationshipId });
                    }
                }

                mainPart.Document!.Save();
            }
            File.Move(temporary, filePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool HeaderContainsOurMarker(HeaderPart headerPart)
    {
        using var stream = headerPart.GetStream(FileMode.Open, FileAccess.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Contains("CompanyDlpWatermark", StringComparison.Ordinal);
    }

    private static string BuildDocxWatermarkHeaderXml(string[] lines)
    {
        var paragraphs = string.Join(Environment.NewLine, lines.Select((line, index) =>
            $$"""
              <w:p>
                <w:pPr><w:pStyle w:val="Header"/><w:jc w:val="right"/></w:pPr>
                {{(index == 0 ? "<w:r><w:rPr><w:vanish/></w:rPr><w:t>CompanyDlpWatermark</w:t></w:r>" : "")}}
                <w:r>
                  <w:rPr><w:b w:val="{{(index == 0 ? "1" : "0")}}"/><w:sz w:val="16"/><w:color w:val="8B1A1A"/></w:rPr>
                  <w:t xml:space="preserve">{{System.Security.SecurityElement.Escape(line)}}</w:t>
                </w:r>
              </w:p>
              """));

        return $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
        {{paragraphs}}
        </w:hdr>
        """;
    }

    // === PPTX: a small, right-aligned, non-rotated text box in the top-right corner of every
    // slide's own shape tree (rather than the slide master) - simpler and more predictable than
    // relying on master/layout inheritance rules. Detects and replaces a previous watermark shape
    // by name.
    private static void WatermarkPptx(string filePath, string[] lines)
    {
        var temporary = filePath + ".tmp";
        File.Copy(filePath, temporary, true);
        try
        {
            using (var document = PresentationDocument.Open(temporary, true))
            {
                var presentationPart = document.PresentationPart
                    ?? throw new InvalidOperationException("Not a valid PowerPoint presentation.");

                foreach (var slidePart in presentationPart.SlideParts)
                {
                    var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
                    if (shapeTree is null) continue;

                    var existing = shapeTree.Elements<P.Shape>().FirstOrDefault(IsOurWatermarkShape);
                    existing?.Remove();

                    shapeTree.AppendChild(new P.Shape(BuildPptxWatermarkShapeXml(lines)));
                    slidePart.Slide!.Save();
                }
            }
            File.Move(temporary, filePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static bool IsOurWatermarkShape(P.Shape shape) =>
        shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "CompanyDlpWatermark";

    private static string BuildPptxWatermarkShapeXml(string[] lines)
    {
        var paragraphs = string.Join(Environment.NewLine, lines.Select((line, index) =>
            $$"""
              <a:p>
                <a:pPr algn="r"/>
                <a:r>
                  <a:rPr lang="en-US" sz="1200" b="{{(index == 0 ? "1" : "0")}}">
                    <a:solidFill><a:srgbClr val="8B1A1A"/></a:solidFill>
                  </a:rPr>
                  <a:t>{{System.Security.SecurityElement.Escape(line)}}</a:t>
                </a:r>
              </a:p>
              """));

        return $$"""
        <p:sp xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:nvSpPr>
            <p:cNvPr id="999001" name="CompanyDlpWatermark"/>
            <p:cNvSpPr/>
            <p:nvPr/>
          </p:nvSpPr>
          <p:spPr>
            <a:xfrm>
              <a:off x="7100000" y="150000"/>
              <a:ext cx="4900000" cy="900000"/>
            </a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
            <a:noFill/>
          </p:spPr>
          <p:txBody>
            <a:bodyPr wrap="square" anchor="t"/>
            <a:lstStyle/>
        {{paragraphs}}
          </p:txBody>
        </p:sp>
        """;
    }

    // === Images: draw the three lines directly onto the pixels, right-aligned in the top-right
    // corner, with an opaque background panel behind them so the text stays legible regardless
    // of what's underneath it in the photo.
    private static void WatermarkImage(string filePath, string[] lines)
    {
        var temporary = filePath + ".tmp";
        using (var original = new Bitmap(filePath))
        {
            using var canvas = new Bitmap(original.Width, original.Height);
            using (var graphics = Graphics.FromImage(canvas))
            {
                graphics.DrawImage(original, 0, 0, original.Width, original.Height);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                var fontSize = Math.Max(8f, Math.Min(original.Width, original.Height) / 20f);
                using var font = new System.Drawing.Font("Arial", fontSize, System.Drawing.FontStyle.Regular);
                using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(235, 139, 26, 26));
                // Fully opaque, not translucent: confirmed live that even a mostly-opaque panel
                // (240/255) still let high-contrast underlying content (black text) show through
                // as a faint ghost, since alpha blending never fully removes a strong-contrast
                // pixel. A small corner label box occluding its corner completely is the correct
                // trade-off here - the same one a real printed stamp on a busy photo would make.
                using var backgroundBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 255, 255, 255));

                var lineSizes = lines.Select(line => graphics.MeasureString(line, font)).ToArray();
                var blockWidth = lineSizes.Max(size => size.Width);
                var blockHeight = lineSizes.Sum(size => size.Height);
                const float margin = 8f;
                var panelLeft = original.Width - blockWidth - margin * 2;
                var panelTop = margin * 0.5f;

                graphics.FillRectangle(backgroundBrush, panelLeft, panelTop, blockWidth + margin * 2, blockHeight + margin);

                var y = panelTop + margin * 0.5f;
                foreach (var (line, size) in lines.Zip(lineSizes))
                {
                    graphics.DrawString(line, font, textBrush, original.Width - size.Width - margin * 1.5f, y);
                    y += size.Height;
                }
            }

            canvas.Save(temporary, GetImageFormat(filePath));
        }
        File.Move(temporary, filePath, true);
    }

    private static ImageFormat GetImageFormat(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            _ => ImageFormat.Png
        };
}
