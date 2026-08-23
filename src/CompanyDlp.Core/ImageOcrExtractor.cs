using System.Drawing;
using System.Drawing.Imaging;
using Tesseract;

namespace CompanyDlp.Core;

// Port of the Python AI-DLP backend's OCR step (pytesseract, lang='eng+ara', config='--oem 3 --psm 4',
// grayscale via PIL img.convert('L')) - runs the native Tesseract engine bundled with this agent
// instead of shelling out to a Python subprocess. Long-lived singleton (native engine construction
// is expensive), mirroring LocalEntityExtractor's file-path-constructor/IDisposable shape.
public sealed class ImageOcrExtractor : IDisposable
{
    private readonly TesseractEngine _engine;

    // TesseractEngine.Process is not safe for concurrent calls from multiple threads (unlike
    // InferenceSession.Run, which is) - this instance is a shared singleton, so serialize access.
    private readonly object _lock = new();

    public ImageOcrExtractor(string tessDataPath, string languages = "eng+ara")
    {
        _engine = new TesseractEngine(tessDataPath, languages, EngineMode.Default) // --oem 3
        {
            DefaultPageSegMode = PageSegMode.SingleColumn // --psm 4
        };
    }

    public string ExtractText(Stream content)
    {
        using var original = new Bitmap(content);
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
