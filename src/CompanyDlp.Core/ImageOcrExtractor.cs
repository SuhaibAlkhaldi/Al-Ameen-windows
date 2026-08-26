using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace CompanyDlp.Core;

// Port of the Python AI-DLP backend's OCR step (pytesseract, lang='eng+ara', config='--oem 3 --psm 4',
// grayscale via PIL img.convert('L')) - runs the native Tesseract engine bundled with this agent
// instead of shelling out to a Python subprocess. Long-lived singleton (native engine construction
// is expensive), mirroring LocalEntityExtractor's file-path-constructor/IDisposable shape.
public sealed class ImageOcrExtractor : IDisposable
{
    private readonly TesseractEngine _engine;
    private readonly ILogger<ImageOcrExtractor>? _logger;

    // TesseractEngine.Process is not safe for concurrent calls from multiple threads (unlike
    // InferenceSession.Run, which is) - this instance is a shared singleton, so serialize access.
    private readonly object _lock = new();

    public ImageOcrExtractor(string tessDataPath, string languages = "eng+ara", ILogger<ImageOcrExtractor>? logger = null)
    {
        _engine = new TesseractEngine(tessDataPath, languages, EngineMode.Default) // --oem 3
        {
            DefaultPageSegMode = PageSegMode.SingleColumn // --psm 4
        };
        _logger = logger;
    }

    public string ExtractText(Stream content)
    {
        // A malformed/truncated/unsupported-codec image must degrade to "no text found" rather than
        // throw - mirrors ExtractPdf/ExtractDocx's "best-effort text, never blow up on one bad file"
        // contract. Found live: System.Drawing.Bitmap's ctor threw ArgumentException on one real
        // Desktop image, and because that exception propagated out of here, FileInventoryScanner's
        // per-file classification failure got treated as a *transient* fail-closed result
        // (ClassificationProviderUnavailableFailClosed is in FileClassificationReasonCodes.
        // TransientFailureReasonCodes) - meaning the scanner retried this same poison file on every
        // tick forever and the rest of the watched folders never got scanned at all.
        Bitmap original;
        try
        {
            original = new Bitmap(content);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            _logger?.LogWarning(ex, "Could not decode image for OCR; treating as no extractable text.");
            return string.Empty;
        }

        using (original)
        {
            using var grayscale = ToGrayscale(original);
            using var buffer = new MemoryStream();
            grayscale.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);

            lock (_lock)
            {
                using var pix = Pix.LoadFromMemory(buffer.ToArray());
                using var page = _engine.Process(pix);
                return page.GetText() ?? string.Empty;
            }
        }
    }

    // ITU-R BT.601 luma weights - the same weights PIL's img.convert('L') uses, so this matches the
    // Python reference's preprocessing exactly rather than approximating it.
    private static Bitmap ToGrayscale(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height);
        using var g = Graphics.FromImage(result);
        var matrix = new ColorMatrix(
        [
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1]
        ]);
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        g.DrawImage(source, new Rectangle(0, 0, source.Width, source.Height),
            0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        return result;
    }

    public void Dispose() => _engine.Dispose();
}
