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
using X = DocumentFormat.OpenXml.Spreadsheet;
using Xdr = DocumentFormat.OpenXml.Drawing.Spreadsheet;
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
// Two-layer visual design, unified 2026-08-26 to share one text/layout system with the on-screen
// watermark (CompanyDlp.Desktop\Watermark\WatermarkWindow.xaml.cs), per explicit request that the
// file watermark "look professional" and match what's already on the employee's screen:
//   1. A small, clearly-legible, non-rotated corner info block (Classification/Device/Last
//      Scanned), in the true tier color - the "what is this file, at a glance" indicator.
//   2. A tiled, repeating background layer covering the whole page/slide/sheet/image, built from
//      the SAME WatermarkPolicy (opacity/fontSize/spacing/prefix/include-flags) and the same
//      device-user-time text shape as the live screen overlay, just tinted with the tier color
//      instead of WatermarkWindow's neutral dark - so a stamped file and a live screen capture of
//      that same file read as "the same watermark system", not two unrelated designs.
// The two layers are deliberately redundant, not alternatives: removing the watermark from a file
// means finding and deleting two separate things scattered across the whole page, not one block.
//
// Every per-format method writes to a temp file first, then File.Move(overwrite) - same
// write-safety convention used throughout this codebase (PolicyStore, FileClassificationCache,
// etc.) - so a crash mid-write can never leave a half-written file in the original's place.
public static class ContentWatermarker
{
    public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".pdf", ".docx", ".pptx", ".xlsx", ".jpg", ".jpeg", ".png"
    };

    public static bool IsSupported(string extension) => SupportedExtensions.Contains(extension);

    // Matches the screen watermark's typeface family - "Segoe UI Semibold" is a standard Windows
    // font (present since Windows 8) rather than something this project bundles, so every format
    // below can reference it by name; GDI+/PdfSharp/OpenXML all fall back to a substitute font
    // silently rather than throwing if it's ever somehow missing, so this is safe even in that case.
    private const string FontFamily = "Segoe UI Semibold";

    private static readonly Dictionary<string, string> LabelsByTier = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = "PUBLIC",
        // Wire value stays "Internal" (ClassificationTiers.Internal constant unchanged) - only the
        // stamped watermark text changed, per explicit request, to "RESTRICTED".
        [ClassificationTiers.Internal] = "RESTRICTED",
        [ClassificationTiers.Secret] = "SECRET",
        [ClassificationTiers.VerySecret] = "VERY SECRET",
    };

    // Exact hex values the user specified (#008000/#FFA500/#FF0000), not a shade/tint of each -
    // used at reduced opacity in the tiled layer (see each format's drawing code) so that layer
    // reads as a classic translucent watermark rather than an opaque stamp, and at full opacity in
    // the single corner block. Internal uses gold #FFD700 instead of pure yellow (#FFFF00) - pure
    // yellow had very low contrast on a white page. First tried amber #CCA300, but the user said it
    // rendered too brown; gold keeps more yellow hue while staying readable on white.
    private static readonly Dictionary<string, (byte R, byte G, byte B)> ColorsByTier = new(StringComparer.OrdinalIgnoreCase)
    {
        [ClassificationTiers.Public] = (0, 128, 0),        // green    #008000
        [ClassificationTiers.Internal] = (255, 215, 0),    // gold     #FFD700
        [ClassificationTiers.Secret] = (255, 165, 0),      // orange   #FFA500
        [ClassificationTiers.VerySecret] = (255, 0, 0),    // red      #FF0000
    };

    // Pre-blends a tier color toward white by the given opacity (0=white, 1=full tier color) -
    // used where the target format has no real alpha-compositing support for text (see
    // BuildDocxTileWatermarkParagraph) and a light tint has to stand in for transparency.
    private static (byte R, byte G, byte B) BlendTowardWhite((byte R, byte G, byte B) color, double opacity) => (
        (byte)Math.Round(255 * (1 - opacity) + color.R * opacity),
        (byte)Math.Round(255 * (1 - opacity) + color.G * opacity),
        (byte)Math.Round(255 * (1 - opacity) + color.B * opacity));

    // Degrees counter-clockwise, matching PdfSharp/System.Drawing/OpenXML's shared convention of
    // "positive = counter-clockwise" for this style of watermark. Matches WatermarkWindow's -18°
    // on-screen tilt (same magnitude; the two rendering stacks don't share a rotation-sign
    // convention, so this is "visually the same tilt", not a bit-for-bit-identical transform).
    private const double RotationDegrees = 18;

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
    // context supplies the interactive user's name/machine for the tiled layer's text - NOT
    // Environment.UserName/Environment.MachineName, since this runs inside CompanyDlp.Service as
    // LocalSystem, where Environment.UserName is "SYSTEM", not the employee actually using the
    // file. FileInventoryScanner already resolves this correctly via InteractiveUserContextProvider
    // for classification requests; the same ClientContext is threaded through here.
    //
    // Status is not a parameter: this is only ever called from the branches that just determined a
    // file's status IS "Up to Date" (see FileInventoryScanner), so the displayed value is always
    // that fixed string - a parameter that can only ever hold one value would be pure ceremony.
    public static bool ApplyWatermark(
        string filePath,
        string classificationTier,
        DateTimeOffset lastScannedUtc,
        ClientContext context,
        WatermarkPolicy watermarkPolicy,
        ILogger logger)
    {
        if (!LabelsByTier.TryGetValue(classificationTier, out var label)) return false;
        if (!ColorsByTier.TryGetValue(classificationTier, out var color)) return false;
        var extension = Path.GetExtension(filePath);
        if (!IsSupported(extension)) return false;

        var lines = BuildInfoLines(label);
        var tileText = BuildWatermarkTileText(context, watermarkPolicy, lastScannedUtc);
        var visuals = ResolveTileVisuals(watermarkPolicy);

        try
        {
            switch (extension.ToLowerInvariant())
            {
                case ".txt": WatermarkTxt(filePath, lines, lastScannedUtc); return true;
                case ".pdf": WatermarkPdf(filePath, lines, tileText, color, visuals); return true;
                case ".docx": WatermarkDocx(filePath, lines, tileText, color, visuals); return true;
                case ".pptx": WatermarkPptx(filePath, lines, tileText, color, visuals); return true;
                case ".xlsx": WatermarkXlsx(filePath, lines, tileText, color, visuals); return true;
                case ".jpg" or ".jpeg" or ".png": WatermarkImage(filePath, lines, tileText, color, visuals); return true;
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
    // Deliberately excludes the scan timestamp (unlike the original design) - see
    // AlreadyHasCurrentPdfWatermark's comment for why baking a constantly-changing timestamp into
    // the corner block's identity caused every routine rescan to stack a fresh stamp on top of the
    // last one. "Last Scanned" is still useful, so it stays in the tiled layer's text instead
    // (see BuildWatermarkTileText), where it does not gate "is this already watermarked".
    private static string[] BuildInfoLines(string label) =>
    [
        $"Classification: {label}",
        $"Device: {Environment.MachineName}",
    ];

    // Mirrors WatermarkWindow.BuildText() (CompanyDlp.Desktop\Watermark) exactly in shape - same
    // parts, same " - " join, same "IncludeSessionId exists on WatermarkPolicy but is never
    // actually appended" behavior (kept for parity with the live screen overlay, not fixed here,
    // since the point of this method is to match that code's behavior, not improve on it
    // unilaterally). The one deliberate difference: username/machine come from the interactive
    // ClientContext (see ApplyWatermark's comment), and "time" is the scan timestamp rather than
    // DateTime.Now, since a stamped file is written once per scan, not re-rendered every second.
    private static string BuildWatermarkTileText(ClientContext context, WatermarkPolicy policy, DateTimeOffset lastScannedUtc)
    {
        var machineName = string.IsNullOrWhiteSpace(context.MachineName) ? Environment.MachineName : context.MachineName;
        var username = StripDomainPrefix(context.Username);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(policy.Prefix)) parts.Add(policy.Prefix.Trim());
        if (policy.IncludeMachineName) parts.Add(machineName);
        if (policy.IncludeUsername && !string.IsNullOrWhiteSpace(username)) parts.Add(username);
        if (policy.IncludeTime) parts.Add(lastScannedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        return parts.Count == 0
            ? $"{machineName} - {lastScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            : string.Join(" - ", parts);
    }

    // WMI's Win32_ComputerSystem.UserName (see InteractiveUserContextProvider) comes back as
    // "MACHINE\username" or "DOMAIN\username" - stripped to the bare username here so the tiled
    // text matches WatermarkWindow's Environment.UserName (which never carries a domain prefix)
    // for a visually identical result between the live screen overlay and a stamped file.
    private static string StripDomainPrefix(string username)
    {
        if (string.IsNullOrEmpty(username)) return username;
        var separatorIndex = username.IndexOf('\\');
        return separatorIndex >= 0 ? username[(separatorIndex + 1)..] : username;
    }

    // Resolves WatermarkPolicy's screen-oriented numbers into the tile layer's actual drawing
    // parameters, with the same floor-clamps WatermarkWindow.RenderWatermarks applies (so a
    // misconfigured policy - e.g. spacing of 0 - can't produce an unreadable solid block of text
    // in either place) - kept in one spot so every format computes this identically.
    private readonly record struct TileVisuals(int FontSize, byte Alpha, int HorizontalSpacing, int VerticalSpacing);

    private static TileVisuals ResolveTileVisuals(WatermarkPolicy policy) => new(
        FontSize: Math.Clamp(policy.FontSize, 15, 24),
        Alpha: (byte)Math.Clamp(policy.Opacity * 255, 28, 105),
        HorizontalSpacing: Math.Max(420, policy.HorizontalSpacing),
        VerticalSpacing: Math.Max(155, policy.VerticalSpacing));

    // === TXT: a plain text block at the top of the file - the only option for a format with no
    // visual/rendering concept at all (there is no "corner" in plain text, and no way to draw a
    // tiled background either - the single block below is TXT's entire watermark, both layers'
    // worth of information condensed into one place). Detected by its fixed shape so
    // reclassification updates in place instead of stacking on every scan. IgnoreCase: confirmed
    // live that without it, a user hand-editing a value in place (e.g. typing "public" over
    // "INTERNAL") produces text this regex no longer recognizes as "our" block, and the next scan
    // prepends a second one instead of replacing it.
    private static readonly Regex TxtMarkerBlock = new(
        @"\AClassification: .*\r?\nDevice: .*\r?\nLast Scanned: .*\r?\n\r?\n",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static void WatermarkTxt(string filePath, string[] lines, DateTimeOffset lastScannedUtc)
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

        var allLines = lines.Append($"Last Scanned: {lastScannedUtc.ToLocalTime():yyyy-MM-dd HH:mm}");
        var block = string.Join(Environment.NewLine, allLines) + Environment.NewLine + Environment.NewLine;
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, block + stripped);
        File.Move(temporary, filePath, true);
    }

    // === PDF: two lines in the page's bottom-right corner via PdfSharp, non-rotated, in the true
    // tier color, drawn directly over the page with NO backing panel. Deliberately has no opaque
    // (or translucent) background: an earlier version painted a solid white panel behind the text
    // first, which reliably hid whatever original page content happened to sit in that corner
    // (confirmed on a real file where the last few words of body text landed under the panel and
    // became unreadable). The user ruled out both keeping an opaque cover and enlarging the page to
    // make room, so original content must stay fully visible - meaning the tradeoff below is
    // intentional and accepted, not an oversight.
    // Trade-off: PdfSharp has no way to locate and erase a specific string already baked into a
    // page's content stream, so on RECLASSIFICATION (tier change on an already-watermarked PDF) the
    // old line's text is not erased - the new line is drawn on top of it, and both remain
    // visible/overlap. This only affects PDFs that get re-watermarked after their tier already
    // changed once; a PDF watermarked for the first time only ever gets one clean pass. PdfPig
    // (this project's other PDF dependency, used by DocumentTextExtractor) is read-only and cannot
    // write, hence PdfSharp here.
    private static void WatermarkPdf(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        // The scanner only calls this when a file is genuinely new or changed (see
        // FileInventoryScanner's persisted-hash check) - but that check can't see a stale scan
        // left over from before it existed, so this stays as a defense-in-depth check: skip the
        // rewrite entirely when the current watermark already matches.
        if (AlreadyHasCurrentPdfWatermark(filePath, lines)) return;

        var temporary = filePath + ".tmp";
        using (var document = PdfReader.Open(filePath, PdfDocumentOpenMode.Modify))
        {
            var font = new XFont(FontFamily, 9, XFontStyleEx.Bold);
            var brush = new XSolidBrush(XColor.FromArgb(255, color.R, color.G, color.B));
            var format = new XStringFormat { Alignment = XStringAlignment.Far, LineAlignment = XLineAlignment.Near };

            foreach (var page in document.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page);

                DrawPdfTilePattern(gfx, page.Width.Point, page.Height.Point, tileText, color, visuals);

                const double margin = 18;
                const double lineHeight = 12;

                // Bottom-right corner: real documents are far more likely to have body text or a
                // title running into the top margin than to have content reaching all the way down
                // to the bottom edge, so this corner collides with actual page content less often
                // (the exact case that motivated this was a PDF whose text started almost at y=0).
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

    // Second, tiled watermark layer - see the class comment. Drawn first (underneath the single
    // readable corner block) in a staggered grid across the whole page, same text/spacing/opacity
    // system as the on-screen watermark.
    private static void DrawPdfTilePattern(XGraphics gfx, double pageWidth, double pageHeight, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        var font = new XFont(FontFamily, visuals.FontSize * 0.6, XFontStyleEx.Bold);
        var brush = new XSolidBrush(XColor.FromArgb(visuals.Alpha, color.R, color.G, color.B));
        var size = gfx.MeasureString(tileText, font);

        var horizontalSpacing = Math.Max(size.Width * 1.3, visuals.HorizontalSpacing * 0.6);
        var verticalSpacing = Math.Max(size.Height * 5, visuals.VerticalSpacing * 0.6);

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
    // checks whether a CURRENT watermark (same classification, same device) is already the last
    // thing on every page. Text extraction follows content-stream draw order here, and the
    // watermark is always the last thing drawn onto each page, so this is a reliable "already up
    // to date" check without needing to parse/diff the actual page content stream.
    //
    // Deliberately compares only the Classification + Device lines, NOT a timestamp - confirmed
    // live 2026-08-26 that comparing a block containing a scan timestamp (which legitimately
    // differs on every single scan tick, even when nothing about the file or its classification
    // actually changed) meant this check could never match, so every routine rescan appended a
    // brand-new stamp on top of the previous one, stacking indefinitely. FileInventoryScanner's own
    // _lastSeenWriteTimes tracking is the primary defense against redundant calls reaching this
    // method at all during normal operation; this check is the second line of defense for cases
    // that bypass it (e.g. an admin clearing the status store to force a rescan) - it only needs to
    // answer "would this be visually identical", and the exact scan timestamp is not part of that.
    private static bool AlreadyHasCurrentPdfWatermark(string filePath, string[] lines)
    {
        var expected = string.Concat(lines);
        try
        {
            using var document = PigPdfDocument.Open(filePath);
            return document.GetPages().All(page => page.Text.Contains(expected, StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    // === DOCX: two small right-aligned lines in the document's own header (so they repeat on
    // every page, in the margin area) - plain Word paragraphs/runs, not a floating shape. This
    // was originally a floating shape centered on the page and rotated, matching the other
    // formats - reverted after confirming live that a floating shape (needed for center
    // positioning/rotation) is directly clickable and deletable from the body view in both Google
    // Docs and genuine Word Online, and every attempt to lock it against that (read-only document
    // protection, range-permission exceptions, shape locks) was silently ignored by both. Plain
    // header text has no such problem: it isn't part of the interactive body view at all in any
    // editor (needs deliberately entering header-edit mode), which is a basic, universally
    // supported document feature rather than an obscure protection mechanism - so this trades
    // away center positioning and rotation for something that's actually hard to touch by
    // accident. Detects a previously-added block via a marker comment so reclassification replaces
    // the text in place.
    private static void WatermarkDocx(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
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
                    writer.Write(BuildDocxWatermarkHeaderXml(lines, tileText, color, visuals));
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

    private static string BuildDocxWatermarkHeaderXml(string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
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
                  <w:rPr><w:rFonts w:ascii="{{FontFamily}}" w:hAnsi="{{FontFamily}}"/><w:b w:val="{{(index == 0 ? "1" : "0")}}"/><w:sz w:val="16"/><w:color w:val="{{hex}}"/></w:rPr>
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
        {{BuildDocxTileWatermarkParagraph(tileText, color, visuals)}}
        </w:hdr>
        """;
    }

    // Second, tiled watermark layer - see the class comment. Unlike the plain-paragraph corner
    // block above, covering the whole page necessarily means a floating shape (anchored relative
    // to the page, behindDoc so it never covers the body's own text) - the same
    // deletion-by-a-deliberate-user characteristic already accepted for the corner block applies
    // here too, but that's fine: the point of this second layer is redundancy (two separate things
    // to find and remove), not achieving what the corner block already couldn't.
    // Plain WordprocessingML run color (<w:color>) has no alpha channel - unlike PDF/image/PPTX,
    // which all draw this layer with true compositing transparency, Word text simply doesn't
    // support that here. Confirmed live 2026-08-26: shipping the full-saturation tier color with
    // no transparency made this layer fully opaque and it visually collided with the document's
    // own body text, unreadable. The standard workaround (the same one Word's own built-in
    // watermark feature uses) is to pre-blend the color toward white instead of relying on real
    // alpha - a light tint reads as "watermark" against a normal white/light page even though it's
    // technically 100% opaque.
    private static string BuildDocxTileWatermarkParagraph(string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        // *3.0 makes the tint noticeably more visible than a literal alpha translation would give
        // (plain text has no real alpha here - see the comment above), clamped to 1.0 so a high
        // configured opacity can never push the blend past the tier color itself and overflow a byte.
        var tint = BlendTowardWhite(color, Math.Min(1.0, visuals.Alpha / 255.0 * 3.0));
        var hex = $"{tint.R:X2}{tint.G:X2}{tint.B:X2}";
        var repeatedLine = string.Join("          ", Enumerable.Repeat(tileText, 2));
        var rotation = (long)(-RotationDegrees * 60000);
        var fontHalfPoints = visuals.FontSize;

        // Rows are spaced out proportionally to the policy's vertical spacing rather than a fixed
        // count, so an admin's spacing setting actually changes the file layer's density the same
        // way it changes the screen overlay's.
        var rowCount = Math.Clamp(3000 / visuals.VerticalSpacing, 6, 14);
        var paragraphs = string.Join(Environment.NewLine, Enumerable.Range(0, rowCount).Select(_ =>
            $$"""
              <w:p>
                <w:pPr><w:jc w:val="center"/><w:spacing w:before="200" w:after="200"/></w:pPr>
                <w:r>
                  <w:rPr><w:rFonts w:ascii="{{FontFamily}}" w:hAnsi="{{FontFamily}}"/><w:b/><w:sz w:val="{{fontHalfPoints}}"/><w:color w:val="{{hex}}"/></w:rPr>
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

    // === PPTX: a text box on every slide's own shape tree (rather than the slide master) -
    // simpler and more predictable than relying on master/layout inheritance rules - rotated via
    // the shape's own transform, translucent tier color via DrawingML's real alpha modifier, no
    // fill behind it. Detects and replaces a previous watermark shape by name.
    private static void WatermarkPptx(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
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
                    shapeTree.AppendChild(new P.Shape(BuildPptxTileWatermarkShapeXml(tileText, color, visuals, slideWidth, slideHeight)));
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

    // Second, tiled watermark layer - see the class comment. A shape covering the entire slide,
    // its text body filled with the tile text repeated across many lines (each line itself
    // repeating the text several times with spacing) - DrawingML text only flows top-to-bottom, so
    // this "wide lines, many of them" approach is how a real 2D tiled look gets approximated
    // without direct pixel-level control the way the PDF/image versions have. Uses DrawingML's
    // real <a:alpha> modifier (unlike DOCX, which has no equivalent for plain text runs), so this
    // one uses the tier color's true opacity rather than a pre-blended tint.
    private static string BuildPptxTileWatermarkShapeXml(string tileText, (byte R, byte G, byte B) color, TileVisuals visuals, long slideWidth, long slideHeight)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var alphaThousandths = (int)(visuals.Alpha / 255.0 * 100000);
        var repeatedLine = string.Join("          ", Enumerable.Repeat(tileText, 3));
        var rowCount = Math.Clamp(3000 / visuals.VerticalSpacing, 6, 14);
        var paragraphs = string.Join(Environment.NewLine, Enumerable.Range(0, rowCount).Select(_ =>
            $$"""
              <a:p>
                <a:pPr algn="ctr"/>
                <a:r>
                  <a:rPr lang="en-US" sz="{{visuals.FontSize * 100}}" b="1">
                    <a:solidFill><a:srgbClr val="{{hex}}"><a:alpha val="{{alphaThousandths}}"/></a:srgbClr></a:solidFill>
                    <a:latin typeface="{{FontFamily}}"/>
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

    // Corner block: right-aligned, non-rotated, no backing fill - matches PDF/images/DOCX.
    // Deliberately has no solid fill behind the text (see WatermarkPdf's comment for why): an
    // opaque panel here would hide whatever slide content sits in this corner. Unlike PDF/images,
    // this is a pure win with no reclassification tradeoff - WatermarkPptx removes the previous
    // watermark shape by name before adding the new one, so there is never any old+new text
    // stacking regardless of background.
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
                    <a:latin typeface="{{FontFamily}}"/>
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

    // === XLSX: a floating DrawingML shape anchored to each worksheet, same idea as the PPTX tile
    // shape - Excel's own header/footer area was deliberately NOT used here, even though it's the
    // closest built-in analog to DOCX's header: a worksheet's header/footer only renders in Print
    // Layout view or an actual print/export, and is completely invisible in the default Normal
    // view almost every user works in day to day - a watermark nobody sees while editing defeats
    // the point. A floating shape, by contrast, is visible immediately in Normal view.
    // Best-effort by design: the shape is anchored over a generous fixed cell range (see
    // AbsoluteAnchorExtent below) covering a typical viewport/print area rather than the sheet's
    // full (functionally unbounded) grid - there is no "whole sheet" to tile the way a PDF page or
    // slide has fixed bounds. Detects and replaces a previous watermark shape by name, the same
    // pattern WatermarkPptx uses, so reclassification does not stack shapes.
    private static void WatermarkXlsx(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        var temporary = filePath + ".tmp";
        File.Copy(filePath, temporary, true);
        try
        {
            using (var document = SpreadsheetDocument.Open(temporary, true))
            {
                var workbookPart = document.WorkbookPart
                    ?? throw new InvalidOperationException("Not a valid Excel workbook.");

                foreach (var worksheetPart in workbookPart.WorksheetParts)
                {
                    ApplyXlsxWorksheetWatermark(worksheetPart, lines, tileText, color, visuals);
                }
            }
            File.Move(temporary, filePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void ApplyXlsxWorksheetWatermark(WorksheetPart worksheetPart, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        var drawingsPart = worksheetPart.DrawingsPart;
        Xdr.WorksheetDrawing worksheetDrawing;

        if (drawingsPart is null)
        {
            drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
            worksheetDrawing = new Xdr.WorksheetDrawing();
            drawingsPart.WorksheetDrawing = worksheetDrawing;

            var drawing = new X.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) };
            worksheetPart.Worksheet.Append(drawing);
        }
        else
        {
            worksheetDrawing = drawingsPart.WorksheetDrawing ??= new Xdr.WorksheetDrawing();
        }

        // Remove any previous watermark anchor(s) by their shape name before adding fresh ones -
        // same "detect and replace, don't stack" pattern as WatermarkPptx.
        foreach (var anchor in worksheetDrawing.Elements<Xdr.TwoCellAnchor>()
                     .Where(a => IsOurXlsxWatermarkAnchor(a, "CompanyDlpWatermark") || IsOurXlsxWatermarkAnchor(a, "CompanyDlpTileWatermark"))
                     .ToList())
        {
            anchor.Remove();
        }

        worksheetDrawing.Append(BuildXlsxTileAnchor(tileText, color, visuals));
        worksheetDrawing.Append(BuildXlsxCornerAnchor(lines, color));

        // Explicit Save() on both modified part roots - matches WatermarkPptx's
        // slidePart.Slide!.Save() and WatermarkDocx's mainPart.Document!.Save() elsewhere in this
        // file; an OpenXmlPartRootElement's in-memory changes are not guaranteed to reach the
        // underlying part stream without it.
        worksheetDrawing.Save(drawingsPart);
        worksheetPart.Worksheet.Save();
    }

    private static bool IsOurXlsxWatermarkAnchor(Xdr.TwoCellAnchor anchor, string shapeName) =>
        anchor.Descendants<Xdr.NonVisualDrawingProperties>().Any(p => p.Name?.Value == shapeName);

    // Covers roughly the first 60 rows x 20 columns from A1 - a generous, fixed viewport/print-area
    // approximation (see the class comment above WatermarkXlsx for why a true infinite tile isn't
    // possible on a grid with no fixed page bounds).
    private static Xdr.TwoCellAnchor BuildXlsxTileAnchor(string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var alphaThousandths = (int)(visuals.Alpha / 255.0 * 100000);
        var repeatedLine = string.Join("          ", Enumerable.Repeat(tileText, 3));
        var rowCount = Math.Clamp(3000 / visuals.VerticalSpacing, 6, 14);
        var paragraphs = string.Join(Environment.NewLine, Enumerable.Range(0, rowCount).Select(_ =>
            $$"""
              <a:p>
                <a:pPr algn="ctr"/>
                <a:r>
                  <a:rPr lang="en-US" sz="{{visuals.FontSize * 100}}" b="1">
                    <a:solidFill><a:srgbClr val="{{hex}}"><a:alpha val="{{alphaThousandths}}"/></a:srgbClr></a:solidFill>
                    <a:latin typeface="{{FontFamily}}"/>
                  </a:rPr>
                  <a:t>{{System.Security.SecurityElement.Escape(repeatedLine)}}</a:t>
                </a:r>
              </a:p>
              """));

        var rotation = (int)(-RotationDegrees * 60000);
        var shapeXml = $$"""
        <xdr:sp xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" macro="" textlink="">
          <xdr:nvSpPr>
            <xdr:cNvPr id="999002" name="CompanyDlpTileWatermark"/>
            <xdr:cNvSpPr/>
          </xdr:nvSpPr>
          <xdr:spPr>
            <a:xfrm rot="{{rotation}}"><a:off x="0" y="0"/><a:ext cx="7000000" cy="9000000"/></a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
            <a:noFill/>
          </xdr:spPr>
          <xdr:txBody>
            <a:bodyPr wrap="square" anchor="ctr"/>
            <a:lstStyle/>
        {{paragraphs}}
          </xdr:txBody>
        </xdr:sp>
        """;

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker { ColumnId = new Xdr.ColumnId("0"), ColumnOffset = new Xdr.ColumnOffset("0"), RowId = new Xdr.RowId("0"), RowOffset = new Xdr.RowOffset("0") },
            new Xdr.ToMarker { ColumnId = new Xdr.ColumnId("20"), ColumnOffset = new Xdr.ColumnOffset("0"), RowId = new Xdr.RowId("60"), RowOffset = new Xdr.RowOffset("0") },
            new Xdr.Shape(shapeXml),
            new Xdr.ClientData())
        { EditAs = Xdr.EditAsValues.Absolute };
    }

    // Small corner block near the top of the visible area (A1-ish), same Classification/Device
    // text as every other format, tier-colored, no fill.
    private static Xdr.TwoCellAnchor BuildXlsxCornerAnchor(string[] lines, (byte R, byte G, byte B) color)
    {
        var hex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        var paragraphs = string.Join(Environment.NewLine, lines.Select(line =>
            $$"""
              <a:p>
                <a:pPr algn="l"/>
                <a:r>
                  <a:rPr lang="en-US" sz="1000" b="1">
                    <a:solidFill><a:srgbClr val="{{hex}}"/></a:solidFill>
                    <a:latin typeface="{{FontFamily}}"/>
                  </a:rPr>
                  <a:t>{{System.Security.SecurityElement.Escape(line)}}</a:t>
                </a:r>
              </a:p>
              """));

        var shapeXml = $$"""
        <xdr:sp xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" macro="" textlink="">
          <xdr:nvSpPr>
            <xdr:cNvPr id="999001" name="CompanyDlpWatermark"/>
            <xdr:cNvSpPr/>
          </xdr:nvSpPr>
          <xdr:spPr>
            <a:xfrm><a:off x="0" y="0"/><a:ext cx="2200000" cy="500000"/></a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
            <a:noFill/>
          </xdr:spPr>
          <xdr:txBody>
            <a:bodyPr wrap="square" anchor="t"/>
            <a:lstStyle/>
        {{paragraphs}}
          </xdr:txBody>
        </xdr:sp>
        """;

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker { ColumnId = new Xdr.ColumnId("0"), ColumnOffset = new Xdr.ColumnOffset("50000"), RowId = new Xdr.RowId("0"), RowOffset = new Xdr.RowOffset("30000") },
            new Xdr.ToMarker { ColumnId = new Xdr.ColumnId("3"), ColumnOffset = new Xdr.ColumnOffset("0"), RowId = new Xdr.RowId("2"), RowOffset = new Xdr.RowOffset("0") },
            new Xdr.Shape(shapeXml),
            new Xdr.ClientData());
    }

    // === Images: draw the corner lines directly onto the pixels, in the bottom-right corner,
    // non-rotated, in the true tier color, with NO backing panel. Deliberately has no opaque
    // background behind the text (see WatermarkPdf's comment for the full reasoning): an opaque
    // panel here would permanently paint over whatever image pixels sit in that corner, which the
    // user ruled out.
    // Trade-off: each watermark pass composites onto whatever is already on disk (including any
    // earlier watermark pass's pixels, since there is no separate "original" copy kept around), so
    // on RECLASSIFICATION the previous pass's corner text is not erased - the new text is drawn on
    // top of it and both remain visible/overlap. A file watermarked for the first time only ever
    // gets one clean pass.
    private static void WatermarkImage(string filePath, string[] lines, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
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

                DrawImageTilePattern(graphics, original.Width, original.Height, tileText, color, visuals);

                var fontSize = Math.Max(8f, Math.Min(original.Width, original.Height) / 26f);
                using var font = new System.Drawing.Font(FontFamily, fontSize, System.Drawing.FontStyle.Bold);
                using var textBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, color.R, color.G, color.B));

                var lineSizes = lines.Select(line => graphics.MeasureString(line, font)).ToArray();
                const float margin = 8f;
                // Bottom-right corner - see WatermarkPdf's comment for why.
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
    // (WatermarkWindow.RenderWatermarks) - same text/spacing/opacity system, alternating row
    // offsets so it doesn't line up into obvious columns.
    private static void DrawImageTilePattern(Graphics graphics, int width, int height, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        var fontSize = Math.Max(8f, Math.Min(width, height) / 45f);
        using var font = new System.Drawing.Font(FontFamily, fontSize, System.Drawing.FontStyle.Bold);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(visuals.Alpha, color.R, color.G, color.B));
        var size = graphics.MeasureString(tileText, font);

        var horizontalSpacing = Math.Max(size.Width * 1.3f, visuals.HorizontalSpacing * 0.9f);
        var verticalSpacing = Math.Max(size.Height * 5f, visuals.VerticalSpacing * 0.9f);

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
