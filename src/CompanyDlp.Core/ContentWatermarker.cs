using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using CompanyDlp.Contracts;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
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
//   1. A small, clearly-legible, non-rotated corner info block (Classification/Device), in the
//      true tier color - the "what is this file, at a glance" indicator. Still plain text in every
//      format (a Word header paragraph, a PDF DrawString call, a slide/sheet shape) since it never
//      needs to rotate and plain text stays crisp/selectable/searchable.
//   2. A tiled, repeating background layer covering the whole page/slide/sheet/image, built from
//      the SAME WatermarkPolicy (opacity/fontSize/spacing/prefix/include-flags) and the same
//      device-user-time text shape as the live screen overlay, tinted with the tier color.
//
// Revised again same day: the tile layer is now rendered ONCE as a single transparent PNG (see
// BuildTileLayerPng) and embedded as a plain, non-rotated floating PICTURE in every format that
// isn't already raster (PDF/DOCX/PPTX/XLSX) - the rotation is baked into the picture's pixels
// instead of relying on each format's own "rotated shape" feature. This replaced two earlier,
// separate vector approaches (PdfSharp's own transform stack for PDF; a DrawingML
// wordprocessingShape/VML shape for DOCX) after live testing on real user-provided files showed
// both were unreliable: the PDF version's tile grid rendered as a small off-center clustered block
// instead of an even full-page pattern (PdfSharp's default transform composition order differs
// from System.Drawing's), and the DOCX version's rotation did not render AT ALL in at least one
// major viewer (LibreOffice) despite spec-valid markup, in two different shape techniques tried.
// A plain embedded picture has neither failure mode - "floating picture behind the text" is a
// basic, universally-supported feature in every one of these formats, so there is no per-viewer
// rendering risk left, and every format now produces the literal same pixels for this layer,
// which is also what makes them "match each other" and match the screen watermark, as requested.
public static class ContentWatermarker
{
    // PdfSharp 6.x has no built-in OS font fallback of its own - by default it leans on GDI+
    // (System.Drawing) font family enumeration, which is a well-known unreliable combination inside
    // a Windows Service running as LocalSystem with no interactive desktop session (Session 0):
    // confirmed live 2026-08-27 that XFont(FontFamily, ...) throws "No appropriate font found for
    // family name 'Segoe UI Semibold'" on some real PDFs but not others in the SAME running service
    // process - a session/threading-dependent GDI+ flake, not a permanently-missing font (the font
    // is genuinely installed; a Word-exported PDF processed moments earlier in the same run
    // embedded it into its own content without issue). Registering this resolver makes font
    // resolution deterministic: it reads the TTF bytes directly off disk instead of asking GDI+ to
    // enumerate installed families, so it no longer depends on that flaky path at all. Assigned once
    // via the static constructor below, before this class's first PDF watermark call.
    private sealed class WindowsFontResolver : IFontResolver
    {
        private const string SegoeUiSemibold = "SegoeUISemibold";
        private static readonly string FontsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public byte[]? GetFont(string faceName)
        {
            // "seguisb.ttf" is the actual on-disk file name Windows ships for the "Segoe UI
            // Semibold" family; falls back to Arial (present on every Windows install) for
            // anything else, or if that exact file is ever missing/renamed - watermarking must
            // never throw again just because one specific font file can't be found.
            var fileName = faceName == SegoeUiSemibold ? "seguisb.ttf" : "arial.ttf";
            var path = Path.Combine(FontsDirectory, fileName);
            if (File.Exists(path)) return File.ReadAllBytes(path);

            var arialFallback = Path.Combine(FontsDirectory, "arial.ttf");
            return File.Exists(arialFallback) ? File.ReadAllBytes(arialFallback) : null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            // "Segoe UI Semibold" has no separate true-bold face on Windows (seguisb.ttf IS the
            // semibold weight) - GDI+ previously simulated (emboldened) it when Bold was
            // requested; MustSimulateBold reproduces that same visual result here.
            return familyName.Equals("Segoe UI Semibold", StringComparison.OrdinalIgnoreCase)
                ? new FontResolverInfo(SegoeUiSemibold, isBold, isItalic)
                : new FontResolverInfo("Arial", isBold, isItalic);
        }
    }

    static ContentWatermarker()
    {
        GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

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

    // Degrees counter-clockwise, matching WatermarkWindow's -18° on-screen tilt (same magnitude;
    // the two rendering stacks don't share a rotation-sign convention bit-for-bit, so this is
    // "visually the same tilt", not a bit-for-bit-identical transform).
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

    // === Shared tile-layer rendering - the one implementation every format's background pattern
    // ultimately goes through (directly for images, via a wrapped PNG for PDF/DOCX/PPTX/XLSX - see
    // BuildTileLayerPng). A plain staggered grid in UNROTATED canvas coordinates spanning the full
    // width/height (with one spacing unit of overscan on every edge so a tile centered near a
    // corner isn't clipped), with only each individual tile's TEXT rotated in place around its own
    // anchor point. This is also what the on-screen watermark does (WatermarkWindow.RenderWatermarks
    // positions a staggered grid of elements, each with its own RotateTransform) - rotating each
    // tile individually, rather than rotating the whole sampling grid as a first attempt at this did
    // for PDF, is what guarantees the pattern trivially reaches every corner of a plain rectangle
    // regardless of the rotation angle chosen.
    private static void DrawTileLayer(Graphics graphics, double widthPixels, double heightPixels, double pixelsPerPoint, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals)
    {
        using var font = new System.Drawing.Font(FontFamily, (float)(visuals.FontSize * pixelsPerPoint), System.Drawing.FontStyle.Bold);
        using var brush = new SolidBrush(System.Drawing.Color.FromArgb(visuals.Alpha, color.R, color.G, color.B));
        var horizontalSpacing = (float)(visuals.HorizontalSpacing * pixelsPerPoint);
        var verticalSpacing = (float)(visuals.VerticalSpacing * pixelsPerPoint);

        var row = 0;
        for (var y = -verticalSpacing; y < heightPixels + verticalSpacing; y += verticalSpacing, row++)
        {
            var rowOffset = row % 2 == 0 ? 0 : horizontalSpacing / 2;
            for (var x = -horizontalSpacing + rowOffset; x < widthPixels + horizontalSpacing; x += horizontalSpacing)
            {
                var state = graphics.Save();
                graphics.TranslateTransform(x, (float)y);
                graphics.RotateTransform((float)-RotationDegrees);
                graphics.DrawString(tileText, font, brush, 0, 0);
                graphics.Restore(state);
            }
        }
    }

    // Renders the tile layer onto a fresh transparent bitmap sized from a physical "points"
    // dimension (matching PDF/DOCX/PPTX page-or-slide units, where 1pt = 1/72in) - pixelsPerPoint
    // trades memory/file size for sharper text; 2.0 (the default used by every page-shaped format)
    // is comfortably sharp for a light background layer without producing an oversized embedded
    // image.
    private static Bitmap BuildTileLayerBitmap(double widthPoints, double heightPoints, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals, double pixelsPerPoint)
    {
        var width = Math.Max(1, (int)Math.Round(widthPoints * pixelsPerPoint));
        var height = Math.Max(1, (int)Math.Round(heightPoints * pixelsPerPoint));
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        DrawTileLayer(graphics, width, height, pixelsPerPoint, tileText, color, visuals);
        return bitmap;
    }

    private static byte[] BuildTileLayerPng(double widthPoints, double heightPoints, string tileText, (byte R, byte G, byte B) color, TileVisuals visuals, double pixelsPerPoint = 2.0)
    {
        using var bitmap = BuildTileLayerBitmap(widthPoints, heightPoints, tileText, color, visuals, pixelsPerPoint);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

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
    // tier color, drawn directly over the page with NO backing panel, plus the shared tile-layer
    // PNG (see BuildTileLayerPng) stretched full-bleed underneath it. Deliberately has no opaque
    // (or translucent) background behind the corner text: an earlier version painted a solid white
    // panel behind the text first, which reliably hid whatever original page content happened to
    // sit in that corner (confirmed on a real file where the last few words of body text landed
    // under the panel and became unreadable). The user ruled out both keeping an opaque cover and
    // enlarging the page to make room, so original content must stay fully visible - meaning the
    // tradeoff below is intentional and accepted, not an oversight.
    // Trade-off: PdfSharp has no way to locate and erase a specific string/image already baked
    // into a page's content stream, so on RECLASSIFICATION (tier change on an already-watermarked
    // PDF) the old layer is not erased - the new one is drawn on top of it. This only affects PDFs
    // that get re-watermarked after their tier already changed once; a PDF watermarked for the
    // first time only ever gets one clean pass. PdfPig (this project's other PDF dependency, used
    // by DocumentTextExtractor) is read-only and cannot write, hence PdfSharp here.
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

            // Cached per distinct page size (almost always identical across a document's pages) so
            // a multi-page PDF doesn't re-render the same bitmap once per page.
            byte[]? cachedTilePng = null;
            double cachedWidth = -1, cachedHeight = -1;

            foreach (var page in document.Pages)
            {
                using var gfx = XGraphics.FromPdfPage(page);

                if (cachedTilePng is null || cachedWidth != page.Width.Point || cachedHeight != page.Height.Point)
                {
                    cachedTilePng = BuildTileLayerPng(page.Width.Point, page.Height.Point, tileText, color, visuals);
                    cachedWidth = page.Width.Point;
                    cachedHeight = page.Height.Point;
                }

                // Not `new MemoryStream(cachedTilePng)` - that overload constructs a
                // non-"publicly visible" stream (_exposable = false), and PdfSharp's XImage.FromStream
                // internally calls MemoryStream.GetBuffer() to read the PNG bytes back out. Confirmed
                // live 2026-08-27: that combination throws "UnauthorizedAccessException: MemoryStream's
                // internal buffer cannot be accessed" for some real PDFs (a 1029-page scanned textbook)
                // but not others - GetBuffer() apparently isn't hit on every code path inside
                // ImportImage, so smaller/simpler images happened to avoid it. The 4-argument
                // constructor with publiclyVisible: true makes GetBuffer() always succeed, regardless
                // of which internal path PdfSharp takes.
                using var tileImageStream = new MemoryStream(cachedTilePng, 0, cachedTilePng.Length, writable: false, publiclyVisible: true);
                using var tileImage = XImage.FromStream(tileImageStream);
                gfx.DrawImage(tileImage, 0, 0, page.Width.Point, page.Height.Point);

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
    // every page, in the margin area) - plain Word paragraphs/runs, not a floating shape - plus the
    // shared tile-layer PNG (see BuildTileLayerPng) embedded as a plain floating picture covering
    // the whole page, behind the body text. This was originally a rotated DrawingML shape with the
    // tile text as live paragraphs - reverted after confirming live on a real user file that its
    // rotation simply did not render in LibreOffice despite spec-valid markup (two different shape
    // techniques were tried; neither rotated). A plain embedded picture has no such per-viewer
    // rendering risk - see the class-level comment for the full reasoning. Detects a
    // previously-added header via a marker comment so reclassification replaces the header (and
    // reuses/overwrites its one image part) in place instead of stacking.
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

                var (pageWidthEmu, pageHeightEmu) = GetDocxPageSizeEmu(body);
                var tilePng = BuildTileLayerPng(pageWidthEmu / 12700.0, pageHeightEmu / 12700.0, tileText, color, visuals);

                var existingHeaderPart = mainPart.HeaderParts.FirstOrDefault(HeaderContainsOurMarker);
                var headerPart = existingHeaderPart ?? mainPart.AddNewPart<HeaderPart>();

                // Reuse the header's existing image part (if this is a reclassification) instead
                // of adding a second one every time - same "detect and replace in place, don't
                // accumulate" contract as every other format below.
                var imagePart = headerPart.ImageParts.FirstOrDefault() ?? headerPart.AddImagePart(ImagePartType.Png);
                using (var imageStream = imagePart.GetStream(FileMode.Create, FileAccess.Write))
                {
                    imageStream.Write(tilePng, 0, tilePng.Length);
                }
                var imageRelId = headerPart.GetIdOfPart(imagePart);

                using (var writer = new StreamWriter(headerPart.GetStream(FileMode.Create, FileAccess.Write)))
                {
                    writer.Write(BuildDocxWatermarkHeaderXml(lines, color, imageRelId, pageWidthEmu, pageHeightEmu));
                }

                // Unconditional, not just "on first creation": confirmed live 2026-08-26 on a real
                // resume template that had <w:titlePg/> set ("Different First Page") - Word shows
                // NOTHING on page 1 unless a "first" header reference is explicitly present, even
                // though a "default" one is set and correctly used for every other page. Our
                // watermark was silently absent from page 1 of exactly that kind of document.
                // Setting BOTH references to the same header part is harmless when titlePg is off
                // (Word simply never looks at the unused "first" reference in that case), and this
                // now runs on every call (not only when the header part is brand new) so a file
                // that was already watermarked before this fix existed gets corrected on its next
                // reclassification pass too, not just on first-ever watermarking.
                //
                // Descendants, not Elements: a document with a section break IN THE MIDDLE of the
                // body (very common in real templates - confirmed live on a second real resume,
                // which had two sections) stores that section's SectionProperties inside the last
                // paragraph BEFORE the break's own ParagraphProperties, not as a direct child of
                // Body - only the final section's SectionProperties is a direct child. Elements<T>()
                // only sees direct children, so it silently found and updated just the LAST
                // section, leaving every page that belonged to the earlier section (everything
                // before the break) with no headerReference at all and therefore no watermark -
                // exactly the "not all pages" symptom reported live. Descendants<T>() walks the
                // whole body and finds every section, wherever its SectionProperties actually lives.
                var relationshipId = mainPart.GetIdOfPart(headerPart);
                var sectionProperties = body.Descendants<SectionProperties>().ToList();
                if (sectionProperties.Count == 0)
                {
                    var newSectionProperties = new SectionProperties();
                    body.Append(newSectionProperties);
                    sectionProperties.Add(newSectionProperties);
                }

                foreach (var sectPr in sectionProperties)
                {
                    sectPr.RemoveAllChildren<HeaderReference>();
                    sectPr.PrependChild(new HeaderReference { Type = HeaderFooterValues.First, Id = relationshipId });
                    sectPr.PrependChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = relationshipId });
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

    // Reads the document's actual page size (falls back to US Letter if a document somehow has no
    // section properties yet) so the embedded tile picture is sized to genuinely cover the real
    // page - a mismatched size would either leave gaps or spill past the edges. Twips (the
    // WordprocessingML unit, 1/20 pt) convert to EMU (DrawingML's unit, 12700 per pt) via *635.
    // Descendants, not Elements: see the comment on the header-reference loop in WatermarkDocx for
    // why a document can have SectionProperties that are NOT direct children of Body.
    private static (long WidthEmu, long HeightEmu) GetDocxPageSizeEmu(Body body)
    {
        var pageSize = body.Descendants<SectionProperties>().FirstOrDefault()?.GetFirstChild<PageSize>();
        var widthTwips = pageSize?.Width?.Value ?? 12240U;  // US Letter default, 8.5in
        var heightTwips = pageSize?.Height?.Value ?? 15840U; // 11in
        return ((long)widthTwips * 635, (long)heightTwips * 635);
    }

    private static string BuildDocxWatermarkHeaderXml(string[] lines, (byte R, byte G, byte B) color, string imageRelId, long pageWidthEmu, long pageHeightEmu)
    {
        // The true, fully-saturated tier color, not a lightened tint - a plain corner marker like
        // this never sits over body content, so there's no readability reason to soften it.
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
               xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture"
               xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
        <!--CompanyDlpWatermark-->
        {{paragraphs}}
        {{BuildDocxTileWatermarkPictureXml(imageRelId, pageWidthEmu, pageHeightEmu)}}
        </w:hdr>
        """;
    }

    // Plain floating picture, full page size, centered on the page, behind the body text - see the
    // class-level comment for why this replaced a rotated DrawingML shape.
    private static string BuildDocxTileWatermarkPictureXml(string imageRelId, long widthEmu, long heightEmu) => $$"""
    <w:p>
      <w:r>
        <w:drawing>
          <wp:anchor behindDoc="1" locked="0" layoutInCell="1" allowOverlap="1" relativeHeight="251658241" simplePos="0">
            <wp:simplePos x="0" y="0"/>
            <wp:positionH relativeFrom="page"><wp:align>center</wp:align></wp:positionH>
            <wp:positionV relativeFrom="page"><wp:align>center</wp:align></wp:positionV>
            <wp:extent cx="{{widthEmu}}" cy="{{heightEmu}}"/>
            <wp:wrapNone/>
            <wp:docPr id="999003" name="CompanyDlpTileWatermark"/>
            <wp:cNvGraphicFramePr/>
            <a:graphic>
              <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                <pic:pic>
                  <pic:nvPicPr>
                    <pic:cNvPr id="0" name="CompanyDlpTileWatermarkImage"/>
                    <pic:cNvPicPr/>
                  </pic:nvPicPr>
                  <pic:blipFill>
                    <a:blip r:embed="{{imageRelId}}"/>
                    <a:stretch><a:fillRect/></a:stretch>
                  </pic:blipFill>
                  <pic:spPr>
                    <a:xfrm><a:off x="0" y="0"/><a:ext cx="{{widthEmu}}" cy="{{heightEmu}}"/></a:xfrm>
                    <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                  </pic:spPr>
                </pic:pic>
              </a:graphicData>
            </a:graphic>
          </wp:anchor>
        </w:drawing>
      </w:r>
    </w:p>
    """;

    // === PPTX: a text box on every slide's own shape tree (rather than the slide master) -
    // simpler and more predictable than relying on master/layout inheritance rules, non-rotated,
    // translucent tier color via DrawingML's real alpha modifier, no fill behind it - plus the
    // shared tile-layer PNG (see BuildTileLayerPng) embedded as a plain picture covering the whole
    // slide, behind everything else. Detects and replaces a previous watermark shape/picture (and
    // its image part) by name, so reclassification does not stack or leave orphaned media parts.
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
                var tilePng = BuildTileLayerPng(slideWidth / 12700.0, slideHeight / 12700.0, tileText, color, visuals);

                foreach (var slidePart in presentationPart.SlideParts)
                {
                    var shapeTree = slidePart.Slide?.CommonSlideData?.ShapeTree;
                    if (shapeTree is null) continue;

                    RemovePptxTileWatermark(slidePart, shapeTree);
                    shapeTree.Elements<P.Shape>().FirstOrDefault(IsOurWatermarkShape)?.Remove();

                    var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                    using (var imageStream = imagePart.GetStream(FileMode.Create, FileAccess.Write))
                    {
                        imageStream.Write(tilePng, 0, tilePng.Length);
                    }
                    var imageRelId = slidePart.GetIdOfPart(imagePart);

                    // Tile picture first (sits behind, since later shapes render on top of earlier
                    // ones in DrawingML's z-order), then the single readable corner block on top.
                    shapeTree.AppendChild(new P.Picture(BuildPptxTileWatermarkPictureXml(imageRelId, slideWidth, slideHeight)));
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

    private static void RemovePptxTileWatermark(SlidePart slidePart, P.ShapeTree shapeTree)
    {
        var existingPicture = shapeTree.Elements<P.Picture>().FirstOrDefault(IsOurTileWatermarkPicture);
        if (existingPicture is null) return;

        var blipId = existingPicture.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault()?.Embed?.Value;
        existingPicture.Remove();
        if (blipId is not null && slidePart.GetPartById(blipId) is ImagePart oldImagePart)
        {
            slidePart.DeletePart(oldImagePart);
        }
    }

    private static bool IsOurWatermarkShape(P.Shape shape) =>
        shape.NonVisualShapeProperties?.NonVisualDrawingProperties?.Name?.Value == "CompanyDlpWatermark";

    private static bool IsOurTileWatermarkPicture(P.Picture picture) =>
        picture.NonVisualPictureProperties?.NonVisualDrawingProperties?.Name?.Value == "CompanyDlpTileWatermark";

    // Plain picture, full slide size, no rotation needed (baked into the pixels already) - see the
    // class-level comment for why this replaced a rotated DrawingML text shape.
    private static string BuildPptxTileWatermarkPictureXml(string imageRelId, long slideWidth, long slideHeight) => $$"""
    <p:pic xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
      <p:nvPicPr>
        <p:cNvPr id="999002" name="CompanyDlpTileWatermark"/>
        <p:cNvPicPr/>
        <p:nvPr/>
      </p:nvPicPr>
      <p:blipFill>
        <a:blip r:embed="{{imageRelId}}"/>
        <a:stretch><a:fillRect/></a:stretch>
      </p:blipFill>
      <p:spPr>
        <a:xfrm><a:off x="0" y="0"/><a:ext cx="{{slideWidth}}" cy="{{slideHeight}}"/></a:xfrm>
        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
      </p:spPr>
    </p:pic>
    """;

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

    // === XLSX: a floating DrawingML picture anchored to each worksheet, same idea as the PPTX tile
    // picture - Excel's own header/footer area was deliberately NOT used here, even though it's the
    // closest built-in analog to DOCX's header: a worksheet's header/footer only renders in Print
    // Layout view or an actual print/export, and is completely invisible in the default Normal
    // view almost every user works in day to day - a watermark nobody sees while editing defeats
    // the point. A floating picture, by contrast, is visible immediately in Normal view.
    // Best-effort by design: the picture is anchored over a generous fixed cell range (see
    // XlsxTileWidthPoints/HeightPoints below) covering a typical viewport/print area rather than
    // the sheet's full (functionally unbounded) grid - there is no "whole sheet" to tile the way a
    // PDF page or slide has fixed bounds. Detects and replaces a previous watermark anchor by name
    // (and deletes its image part), the same pattern WatermarkPptx uses, so reclassification does
    // not stack shapes or leave orphaned media parts.
    private const double XlsxTileWidthPoints = 960;  // ~20 default-width columns
    private const double XlsxTileHeightPoints = 900; // ~60 default-height rows

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
                var tilePng = BuildTileLayerPng(XlsxTileWidthPoints, XlsxTileHeightPoints, tileText, color, visuals);

                foreach (var worksheetPart in workbookPart.WorksheetParts)
                {
                    ApplyXlsxWorksheetWatermark(worksheetPart, lines, tilePng, color, visuals);
                }
            }
            File.Move(temporary, filePath, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static void ApplyXlsxWorksheetWatermark(WorksheetPart worksheetPart, string[] lines, byte[] tilePng, (byte R, byte G, byte B) color, TileVisuals visuals)
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
        // same "detect and replace, don't stack" pattern as WatermarkPptx - and delete the old
        // tile's image part too, so reclassification doesn't leave orphaned media parts behind.
        foreach (var anchor in worksheetDrawing.Elements<Xdr.TwoCellAnchor>()
                     .Where(a => IsOurXlsxWatermarkAnchor(a, "CompanyDlpWatermark") || IsOurXlsxWatermarkAnchor(a, "CompanyDlpTileWatermark"))
                     .ToList())
        {
            var blipId = anchor.Descendants<DocumentFormat.OpenXml.Drawing.Blip>().FirstOrDefault()?.Embed?.Value;
            anchor.Remove();
            if (blipId is not null && drawingsPart.GetPartById(blipId) is ImagePart oldImagePart)
            {
                drawingsPart.DeletePart(oldImagePart);
            }
        }

        var imagePart = drawingsPart.AddImagePart(ImagePartType.Png);
        using (var imageStream = imagePart.GetStream(FileMode.Create, FileAccess.Write))
        {
            imageStream.Write(tilePng, 0, tilePng.Length);
        }
        var imageRelId = drawingsPart.GetIdOfPart(imagePart);

        worksheetDrawing.Append(BuildXlsxTileAnchor(imageRelId));
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
    // possible on a grid with no fixed page bounds). No rotation attribute needed - baked into the
    // picture's pixels already.
    private static Xdr.TwoCellAnchor BuildXlsxTileAnchor(string imageRelId)
    {
        var extCx = (long)(XlsxTileWidthPoints * 12700);
        var extCy = (long)(XlsxTileHeightPoints * 12700);
        var shapeXml = $$"""
        <xdr:pic xmlns:xdr="http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <xdr:nvPicPr>
            <xdr:cNvPr id="999002" name="CompanyDlpTileWatermark"/>
            <xdr:cNvPicPr/>
          </xdr:nvPicPr>
          <xdr:blipFill>
            <a:blip r:embed="{{imageRelId}}"/>
            <a:stretch><a:fillRect/></a:stretch>
          </xdr:blipFill>
          <xdr:spPr>
            <a:xfrm><a:off x="0" y="0"/><a:ext cx="{{extCx}}" cy="{{extCy}}"/></a:xfrm>
            <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
          </xdr:spPr>
        </xdr:pic>
        """;

        return new Xdr.TwoCellAnchor(
            new Xdr.FromMarker { ColumnId = new Xdr.ColumnId("0"), ColumnOffset = new Xdr.ColumnOffset("0"), RowId = new Xdr.RowId("0"), RowOffset = new Xdr.RowOffset("0") },
            new Xdr.ToMarker { ColumnId = new Xdr.ColumnId("20"), ColumnOffset = new Xdr.ColumnOffset("0"), RowId = new Xdr.RowId("60"), RowOffset = new Xdr.RowOffset("0") },
            new Xdr.Picture(shapeXml),
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
    // non-rotated, in the true tier color, with NO backing panel, plus the shared tile layer (see
    // DrawTileLayer) drawn straight onto the same canvas - no intermediate bitmap/embed step needed
    // here since this format already IS a raster canvas.
    // Deliberately has no opaque background behind the corner text (see WatermarkPdf's comment for
    // the full reasoning): an opaque panel here would permanently paint over whatever image pixels
    // sit in that corner, which the user ruled out.
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

                // pixelsPerPoint = 1: an image has no physical "points" unit, so treat the policy's
                // spacing/font numbers as pixels directly - the same 1:1 treatment this method used
                // before this layer was unified with the other formats.
                DrawTileLayer(graphics, original.Width, original.Height, 1.0, tileText, color, visuals);

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

    private static ImageFormat GetImageFormat(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".jpg" or ".jpeg" => ImageFormat.Jpeg,
            _ => ImageFormat.Png
        };
}
