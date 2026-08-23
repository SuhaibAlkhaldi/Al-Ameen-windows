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
        [ClassificationTiers.Internal] = "INTERNAL",
        [ClassificationTiers.Secret] = "SECRET",
        [ClassificationTiers.VerySecret] = "VERY SECRET",
    };

    // Exact hex values the user specified (#008000/#FFA500/#FF0000), not a shade/tint of each -
    // used at reduced opacity in the tiled Method 2 layer (see each format's drawing code) so that
    // layer reads as a classic translucent watermark rather than an opaque stamp, and at full
    // opacity with an opaque white backing panel in Method 1 (the single corner block).
    // Internal uses gold #FFD700 instead of pure yellow (#FFFF00) - pure yellow had very low
    // contrast on the white panel and was barely legible. First tried amber #CCA300, but the user
    // said it rendered too brown; gold keeps more yellow hue while staying readable on white.
    private static readonly Dictionary<string, (byte R, byte G, byte B)> ColorsByTier = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = (0, 128, 0),        // green    #008000
        [ClassificationTiers.Internal] = (255, 215, 0),    // gold     #FFD700
        [ClassificationTiers.Secret] = (255, 165, 0),      // orange   #FFA500
        [ClassificationTiers.VerySecret] = (255, 0, 0),    // red      #FF0000
    };

    // Degrees counter-clockwise, matching PdfSharp/System.Drawing/OpenXML's shared convention of
    // "positive = counter-clockwise" for this style of watermark - a fixed, modest tilt (not the
    // full diagonal the very first design used), the same angle across every format.
    private const double RotationDegrees = 15;

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
        if (!ColorsByTier.TryGetValue(classificationTier, out var color)) return false;
        var extension = Path.GetExtension(filePath);
        if (!IsSupported(extension)) return false;

        var lines = BuildInfoLines(label, lastScannedUtc);
        var tileText = BuildTileText(label, lastScannedUtc);

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".txt": WatermarkTxt(filePath, lines); return true;
                case ".pdf": WatermarkPdf(filePath, lines, tileText, color); return true;
                case ".docx": WatermarkDocx(filePath, lines, tileText, color); return true;
                case ".pptx": WatermarkPptx(filePath, lines, tileText, color); return true;
                case ".jpg" or ".jpeg" or ".png": WatermarkImage(filePath, lines, tileText, color); return true;
                default: return false;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not apply a content watermark to {Path}; leaving its content unchanged.", filePath);
            return false;
        }
    }

    // Device name, not "Status: Up to Date" - Status was dropped because a document already
    // shows a watermark at all only when it WAS successfully classified as up to date (see the
    // class-level comment), so the word "Status" next to a fixed value was redundant; knowing
    // which machine a document was watermarked on is the more useful fact for this same slot.
    private static string[] BuildInfoLines(string label, DateTimeOffset lastScannedUtc) =>
    [
        $"Classification: {label}",
        $"Device: {Environment.MachineName}",
        $"Last Scanned: {lastScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
    ];

    // Second, independent watermark layer requested alongside the single info block above: the
    // same "tiled, repeating text covering the whole page" look the on-screen watermark
    // (CompanyDlp.Desktop\Watermark) already uses, baked into the file itself this time instead of
    // a live overlay. The two layers are deliberately redundant, not alternatives - the point is
    // that removing the watermark from a file now means finding and deleting two separate things
    // scattered across the whole page, not one block in one spot.
    private static string BuildTileText(string label, DateTimeOffset lastScannedUtc) =>
        $"Classification: {label}   •   Last Scanned: {lastScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    // === TXT: a plain text block at the top of the file - the only option for a format with no
    // visual/rendering concept at all (there is no "corner" in plain text). Just the same three
    // lines used everywhere else (Classification/Device/Last Scanned), no extra label - detected
    // by that fixed three-line shape itself so reclassification updates in place instead of
    // stacking on every scan. IgnoreCase: confirmed live that without it, a user hand-editing a
    // value in place (e.g. typing "public" over "INTERNAL") produces text this regex no longer
    // recognizes as "our" block, and the next scan prepends a second one instead of replacing it.
    // The middle line's literal text MUST stay in sync with BuildInfoLines below - confirmed live
    // that letting them drift (this said "Status:" after BuildInfoLines switched to "Device:")
    // makes the regex never match existing blocks again, so every scan stacks a fresh one on top
    // instead of replacing it - which is exactly how these files ended up with a dozen copies.
    private static readonly Regex TxtMarkerBlock = new(
        @"\AClassification: .*\r?\nDevice: .*\r?\nLast Scanned: .*\r?\n\r?\n",
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

    // === PDF: three lines in the page's top-right corner via PdfSharp, non-rotated, in the true
    // tier color, drawn directly over the page with NO backing panel - matches the other formats'
    // corner-positioned Method 1. Deliberately has no opaque (or translucent) background: an earlier
    // version painted a solid white panel behind the text first, which reliably hid whatever original
    // page content happened to sit in that corner (confirmed on a real file where the last few words
    // of body text landed under the panel and became unreadable). The user ruled out both keeping an
    // opaque cover and enlarging the page to make room, so original content must stay fully visible -
    // meaning the tradeoff below is intentional and accepted, not an oversight.
    // Trade-off: PdfSharp has no way to locate and erase a specific string already baked into a page's
    // content stream, so on RECLASSIFICATION (tier change on an already-watermarked PDF) the old
    // line's text is not erased - the new line is drawn on top of it, and both remain visible/overlap.
    // This only affects PDFs that get re-watermarked after their tier already changed once; a PDF
    // watermarked for the first time only ever gets one clean pass. PdfPig (this project's other PDF
    // dependency, used by DocumentTextExtractor) is read-only and cannot write, hence PdfSharp here.
    private static void WatermarkPdf(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color)
    {
        // The scanner only calls this when a file is genuinely new or changed (see
        // FileInventoryScanner's persisted-hash check) - but that check can't see a stale scan
        // left over from before it existed, so this stays as a defense-in-depth check: skip the
        // rewrite entirely when the current watermark already matches.
        if (AlreadyHasCurrentPdfWatermark(filePath, lines)) return;

        var temporary = filePath + ".tmp";
        using (var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify))
        {
            var font = new XFont("Arial", 9, XFontStyleEx.Bold);
            var brush = new XSolidBrush(XColor.FromArgb(255, color.R, color.G, color.B));
            var format = new XStringFormat { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Near };

            foreach (var page in document.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page);

                DrawPdfTilePattern(gfx, page.Width.Point, page.Height.Point, tileText, color);

                const double margin = 18;
                const double lineHeight = 12;

                // Bottom-right corner rather than top-right: real documents are far more likely to
                // have body text or a title running into the top margin than to have content
                // reaching all the way down to the bottom edge, so this corner collides with actual
                // page content less often (the exact case that motivated moving it was a PDF whose
                // text started almost at y=0).
                var y = page.Height.Point - margin - lines.Length * lineHeight;
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

    // Second, tiled watermark layer - see BuildTileText's comment. Drawn first (underneath the
    // single readable block) in a staggered grid across the whole page, same pattern as the
    // on-screen watermark and the image format's version of this.
    private static void DrawPdfTilePattern(XGraphics gfx, double pageWidth, double pageHeight, string tileText, (byte R, byte G, byte B) color)
    {
        var font = new XFont("Arial", 8, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(70, color.R, color.G, color.B));
        var size = gfx.MeasureString(tileText, font);

        var horizontalSpacing = Math.Max(size.Width * 1.6, 160);
        var verticalSpacing = Math.Max(size.Height * 4, 55);

        gfx.TranslateTransform(pageWidth / 2, pageHeight / 2);
        gfx.RotateTransform(-RotationDegrees);

        var diagonal = Math.Sqrt(pageWidth * pageWidth + pageHeight * pageHeight);
        var row = 0;
        for (var y = -diagonal; y < diagonal; y += verticalSpacing, row++)
        {
            var rowOffset = row % 2 == 0 ? 0 : horizontalSpacing / 2;
            for (var x = -diagonal + rowOffset; x < diagonal; x += horizontalSpacing)
            {
                gfx.DrawString(tileText, font, brush, new XPoint(x, y));
            }
        }

        gfx.RotateTransform(RotationDegrees);
        gfx.TranslateTransform(-pageWidth / 2, -pageHeight / 2);
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
    // every page, in the margin area) - plain Word paragraphs/runs, not a floating shape. This
    // was originally a floating shape centered on the page and tilted 15 degrees, matching the
    // other formats - reverted after confirming live that a floating shape (needed for center
    // positioning/rotation) is directly clickable and deletable from the body view in both Google
    // Docs and genuine Word Online, and every attempt to lock it against that (read-only document
    // protection, range-permission exceptions, shape locks) was silently ignored by both. Plain
    // header text has no such problem: it isn't part of the interactive body view at all in any
    // editor (needs deliberately entering header-edit mode), which is a basic, universally
    // supported document feature rather than an obscure protection mechanism - so this trades
    // away center positioning and rotation for something that's actually hard to touch by
    // accident. Detects a previously-added block via a hidden ("vanish") marker run so
    // reclassification replaces the text in place.
    private static void WatermarkDocx(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color)
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
                    writer.Write(BuildDocxWatermarkHeaderXml(lines, tileText, color));
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

    private static string BuildDocxWatermarkHeaderXml(string[] lines, string tileText, (byte R, byte G, byte B) color)
    {
        // The true, fully-saturated tier color, not a lightened tint - a plain corner marker like
        // this never sits over body content, so there's no readability reason to soften it the
        // way the center-of-page floating shape below needs to.
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";

        var paragraphs = string.Join(Environment.NewLine, lines.Select((line, index) =>
            $$"""
              <w:p>
                <w:pPr><w:pStyle w:val="Header"/><w:jc w:val="right"/></w:pPr>
                <w:r>
                  <w:rPr><w:b w:val="{{(index == 0 ? "1" : "0")}}"/><w:sz w:val="16"/><w:color w:val="{{hex}}"/></w:rPr>
                  <w:t xml:space="preserve">{{System.Security.SecurityElement.Escape(line)}}</w:t>
                </w:r>
              </w:p>
              """));

        // The marker is an XML comment, not a hidden ("vanish") text run: confirmed live that
        // neither Google Docs' importer nor Word Online reliably honor vanish formatting, so the
        // "hidden" marker text was rendering as visible garbage in the header. A comment is a
        // different kind of node entirely - outside the document's content model - so no
        // conformant viewer can ever render it, regardless of how well it implements formatting.
        return $$"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
               xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
               xmlns:wps="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
        <!--CompanyDlpWatermark-->
        {{paragraphs}}
        {{BuildDocxTileWatermarkParagraph(tileText, color)}}
        </w:hdr>
        """;
    }

    // Second, tiled watermark layer - see BuildTileText's comment. Unlike the plain-paragraph
    // corner block above, covering the whole page necessarily means a floating shape (anchored
    // relative to the page, behindDoc so it never covers the body's own text) - the same
    // deletion-by-a-deliberate-user characteristic already accepted for the corner block applies
    // here too, but that's fine: the point of this second layer is redundancy (two separate things
    // to find and remove), not achieving what the corner block already couldn't.
    private static string BuildDocxTileWatermarkParagraph(string tileText, (byte R, byte G, byte B) color)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var repeatedLine = string.Join("     ", Enumerable.Repeat(tileText, 3));
        var rotation = (long)(-RotationDegrees * 60000);

        var paragraphs = string.Join(Environment.NewLine, Enumerable.Range(0, 18).Select(_ =>
            $$"""
              <w:p>
                <w:pPr><w:jc w:val="center"/></w:pPr>
                <w:r>
                  <w:rPr><w:b/><w:sz w:val="14"/><w:color w:val="{{hex}}"/></w:rPr>
                  <w:t xml:space="preserve">{{System.Security.SecurityElement.Escape(repeatedLine)}}</w:t>
                </w:r>
              </w:p>
              """));

        return $$"""
        <w:p>
          <w:r>
            <w:drawing>
              <wp:anchor behindDoc="1" locked="0" layoutInCell="1" allowOverlap="1" relativeHeight="251658241" simplePos="0">
                <wp:simplePos x="0" y="0"/>
                <wp:positionH relativeFrom="page"><wp:align>center</wp:align></wp:positionH>
                <wp:positionV relativeFrom="page"><wp:align>center</wp:align></wp:positionV>
                <wp:extent cx="9000000" cy="9000000"/>
                <wp:wrapNone/>
                <wp:docPr id="999003" name="CompanyDlpTileWatermark"/>
                <wp:cNvGraphicFramePr/>
                <a:graphic>
                  <a:graphicData uri="http://schemas.microsoft.com/office/word/2010/wordprocessingShape">
                    <wps:wsp>
                      <wps:cNvSpPr/>
                      <wps:spPr>
                        <a:xfrm rot="{{rotation}}">
                          <a:off x="0" y="0"/>
                          <a:ext cx="9000000" cy="9000000"/>
                        </a:xfrm>
                        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                        <a:noFill/>
                      </wps:spPr>
                      <wps:txbx>
                        <w:txbxContent>
        {{paragraphs}}
                        </w:txbxContent>
                      </wps:txbx>
                      <wps:bodyPr wrap="square" lIns="0" tIns="0" rIns="0" bIns="0" anchor="ctr"/>
                    </wps:wsp>
                  </a:graphicData>
                </a:graphic>
              </wp:anchor>
            </w:drawing>
          </w:r>
        </w:p>
        """;
    }

    // === PPTX: a text box centered on every slide's own shape tree (rather than the slide
    // master) - simpler and more predictable than relying on master/layout inheritance rules -
    // tilted 15 degrees via the shape's own transform, translucent tier color via DrawingML's
    // alpha modifier, no fill behind it. Detects and replaces a previous watermark shape by name.
    private static void WatermarkPptx(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color)
    {
        var temporary = filePath + ".tmp";
        File.Copy(filePath, temporary, true);
        try
        {
            using (var document = PresentationDocument.Open(temporary, true))
            {
                var presentationPart = document.PresentationPart
                    ?? throw new InvalidOperationException("Not a valid PowerPoint presentation.");
                var slideSize = presentationPart.Presentation.SlideSize;
                var slideWidth = slideSize?.Cx?.Value ?? 9144000L;
                var slideHeight = slideSize?.Cy?.Value ?? 6858000L;

                foreach (var slidePart in presentationPart.SlideParts)
                {
                    var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
                    if (shapeTree is null) continue;

                    shapeTree.Elements<P.Shape>().FirstOrDefault(IsOurWatermarkShape)?.Remove();
                    shapeTree.Elements<P.Shape>().FirstOrDefault(IsOurTileWatermarkShape)?.Remove();

                    // Tile layer first (appended first = sits behind, since later shapes render on
                    // top in DrawingML's z-order), then the single readable block on top of it.
                    shapeTree.AppendChild(new P.Shape(BuildPptxTileWatermarkShapeXml(tileText, color, slideWidth, slideHeight)));
                    shapeTree.AppendChild(new P.Shape(BuildPptxWatermarkShapeXml(lines, color, slideWidth, slideHeight)));
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

    private static bool IsOurTileWatermarkShape(P.Shape shape) =>
        shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "CompanyDlpTileWatermark";

    // Second, tiled watermark layer - see BuildTileText's comment. A shape covering the entire
    // slide, its text body filled with the tile text repeated across many lines (each line itself
    // repeating the text several times with spacing) - DrawingML text only flows top-to-bottom, so
    // this "wide lines, many of them" approach is how a real 2D tiled look gets approximated
    // without direct pixel-level control the way the PDF/image versions have.
    private static string BuildPptxTileWatermarkShapeXml(string tileText, (byte R, byte G, byte B) color, long slideWidth, long slideHeight)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var repeatedLine = string.Join("     ", Enumerable.Repeat(tileText, 4));
        var paragraphs = string.Join(Environment.NewLine, Enumerable.Range(0, 14).Select(_ =>
            $$"""
              <a:p>
                <a:pPr algn="ctr"/>
                <a:r>
                  <a:rPr lang="en-US" sz="900" b="1">
                    <a:solidFill><a:srgbClr val="{{hex}}"><a:alpha val="35000"/></a:srgbClr></a:solidFill>
                  </a:rPr>
                  <a:t>{{System.Security.SecurityElement.Escape(repeatedLine)}}</a:t>
                </a:r>
              </a:p>
              """));

        var rotation = (long)(-RotationDegrees * 60000);

        return $$"""
        <p:sp xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:nvSpPr>
            <p:cNvPr id="999002" name="CompanyDlpTileWatermark"/>
            <p:cNvSpPr/>
            <p:nvPr/>
          </p:nvSpPr>
          <p:spPr>
            <a:xfrm rot="{{rotation}}">
              <a:off x="{{-slideWidth / 4}}" y="{{-slideHeight / 4}}"/>
              <a:ext cx="{{slideWidth * 3 / 2}}" cy="{{slideHeight * 3 / 2}}"/>
            </a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
            <a:noFill/>
          </p:spPr>
          <p:txBody>
            <a:bodyPr wrap="square" anchor="ctr"/>
            <a:lstStyle/>
        {{paragraphs}}
          </p:txBody>
        </p:sp>
        """;
    }

    // Method 1: corner-positioned, non-rotated, no backing fill - matches PDF/images/DOCX. Moved
    // back from an earlier center/rotated/translucent design once the separate tiled Method 2
    // (above) took over covering the whole slide. Deliberately has no solid fill behind the text
    // (see WatermarkPdf's comment for why): an opaque panel here would hide whatever slide content
    // sits in this corner. Unlike PDF/images, this is a pure win with no reclassification tradeoff -
    // WatermarkPptx removes the previous watermark shape by name before adding the new one, so there
    // is never any old+new text stacking regardless of background.
    private static string BuildPptxWatermarkShapeXml(string[] lines, (byte R, byte G, byte B) color, long slideWidth, long slideHeight)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var paragraphs = string.Join(Environment.NewLine, lines.Select(line =>
            $$"""
              <a:p>
                <a:pPr algn="r"/>
                <a:r>
                  <a:rPr lang="en-US" sz="1200" b="1">
                    <a:solidFill><a:srgbClr val="{{hex}}"/></a:solidFill>
                  </a:rPr>
                  <a:t>{{System.Security.SecurityElement.Escape(line)}}</a:t>
                </a:r>
              </a:p>
              """));

        const long shapeWidth = 4900000L;
        const long shapeHeight = 900000L;
        var offsetX = slideWidth - shapeWidth - 150000;
        // Bottom-right corner rather than top-right - see WatermarkPdf's comment for why.
        var offsetY = slideHeight - shapeHeight - 150000;

        return $$"""
        <p:sp xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:nvSpPr>
            <p:cNvPr id="999001" name="CompanyDlpWatermark"/>
            <p:cNvSpPr/>
            <p:nvPr/>
          </p:nvSpPr>
          <p:spPr>
            <a:xfrm>
              <a:off x="{{offsetX}}" y="{{offsetY}}"/>
              <a:ext cx="{{shapeWidth}}" cy="{{shapeHeight}}"/>
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

    // === Images: draw the three lines directly onto the pixels, in the top-right corner,
    // non-rotated, in the true tier color, with NO backing panel - matches the other formats'
    // corner-positioned Method 1. Deliberately has no opaque background behind the text (see
    // WatermarkPdf's comment for the full reasoning): an opaque panel here would permanently paint
    // over whatever image pixels sit in that corner, which the user ruled out.
    // Trade-off: each watermark pass composites onto whatever is already on disk (including any
    // earlier watermark pass's pixels, since there is no separate "original" copy kept around), so
    // on RECLASSIFICATION the previous pass's corner text is not erased - the new text is drawn on
    // top of it and both remain visible/overlap. A file watermarked for the first time only ever
    // gets one clean pass.
    private static void WatermarkImage(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color)
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

                DrawImageTilePattern(graphics, original.Width, original.Height, tileText, color);

                var fontSize = Math.Max(8f, Math.Min(original.Width, original.Height) / 26f);
                using var font = new System.Drawing.Font("Arial", fontSize, System.Drawing.FontStyle.Bold);
                using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, color.R, color.G, color.B));

                var lineSizes = lines.Select(line => graphics.MeasureString(line, font)).ToArray();
                const float margin = 8f;
                // Bottom-right corner rather than top-right - see WatermarkPdf's comment for why.
                var blockHeight = lineSizes.Sum(size => size.Height);
                var y = original.Height - blockHeight - margin * 0.5f;
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

    // Staggered tiled grid, matching CompanyDlp.Desktop's on-screen watermark pattern
    // (WatermarkWindow.RenderWatermarks) - alternating row offsets so it doesn't line up into
    // obvious columns, low opacity since it covers most of the image rather than one small spot.
    private static void DrawImageTilePattern(Graphics graphics, int width, int height, string tileText, (byte R, byte G, byte B) color)
    {
        var fontSize = Math.Max(8f, Math.Min(width, height) / 30f);
        using var font = new System.Drawing.Font("Arial", fontSize, System.Drawing.FontStyle.Bold);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(60, color.R, color.G, color.B));
        var size = graphics.MeasureString(tileText, font);

        var horizontalSpacing = Math.Max(size.Width * 1.6f, 260f);
        var verticalSpacing = Math.Max(size.Height * 4f, 110f);

        var state = graphics.Save();
        graphics.RotateTransform((float)-RotationDegrees);

        var diagonal = (float)Math.Sqrt((double)width * width + (double)height * height);
        var row = 0;
        for (var y = -diagonal; y < diagonal; y += verticalSpacing, row++)
        {
            var rowOffset = row % 2 == 0 ? 0 : horizontalSpacing / 2;
            for (var x = -diagonal + rowOffset; x < diagonal; x += horizontalSpacing)
            {
                graphics.DrawString(tileText, font, brush, x, y);
            }
        }

        graphics.Restore(state);
    }

    private static ImageFormat GetImageFormat(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            _ => ImageFormat.Png
        };
}
