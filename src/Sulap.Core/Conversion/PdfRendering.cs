using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;
using WinPdfPageRenderOptions = Windows.Data.Pdf.PdfPageRenderOptions;

namespace Sulap.Core.Conversion;

/// <summary>A rendered page: an uncompressed BMP and the resolution it was rendered at.</summary>
internal sealed record RenderedPage(byte[] Bmp, uint Width, uint Height, double Dpi);

/// <summary>Renders PDF pages with Windows' built-in PDF engine (Windows.Data.Pdf).</summary>
internal sealed class PdfPageRenderer : IDisposable
{
    /// <summary>Longest side of a rendered page; bigger pages (posters) render at a lower resolution.</summary>
    public const uint MaxSide = 8000;

    private const int ErrorWrongPassword = unchecked((int)0x8007052B);

    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    private readonly WinPdfDocument _document;

    private PdfPageRenderer(FileStream file, IRandomAccessStream stream, WinPdfDocument document)
    {
        _file = file;
        _stream = stream;
        _document = document;
    }

    public int PageCount => (int)_document.PageCount;

    public static async Task<PdfPageRenderer> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var stream = file.AsRandomAccessStream();
        try
        {
            var document = await WinPdfDocument.LoadFromStreamAsync(stream).AsTask(cancellationToken);
            return new PdfPageRenderer(file, stream, document);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stream.Dispose();
            await file.DisposeAsync();
            throw ex.HResult == ErrorWrongPassword
                ? PdfErrors.PasswordProtected(path, ex)
                : PdfErrors.Unreadable(path, ex);
        }
    }

    /// <param name="pageIndex">Zero-based.</param>
    public async Task<RenderedPage> RenderAsync(int pageIndex, int dpi, uint maxSide, CancellationToken cancellationToken)
    {
        using var page = _document.GetPage((uint)pageIndex);
        // Page sizes are in device-independent pixels: 96 per inch.
        var widthInches = (double)page.Size.Width / 96;
        var heightInches = (double)page.Size.Height / 96;
        var effectiveDpi = Math.Min(dpi, maxSide / Math.Max(widthInches, heightInches));
        var options = new WinPdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(widthInches * effectiveDpi)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(heightInches * effectiveDpi)),
            BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },
            BitmapEncoderId = BitmapEncoder.BmpEncoderId,
        };

        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options).AsTask(cancellationToken);
        var bytes = new byte[output.Size];
        output.Seek(0);
        await using (var reader = output.AsStreamForRead())
        {
            await reader.ReadExactlyAsync(bytes, cancellationToken);
        }

        return new RenderedPage(bytes, options.DestinationWidth, options.DestinationHeight, effectiveDpi);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _file.Dispose();
    }
}

/// <summary>Text recognition with Windows.Media.Ocr, for scanned pages that have no text layer.</summary>
internal static class PageOcr
{
    /// <summary>A recognizer for the user's languages, or null when Windows has no OCR language for them.</summary>
    public static OcrEngine? CreateEngine()
    {
        try
        {
            return OcrEngine.TryCreateFromUserProfileLanguages();
        }
        catch (Exception ex) when (ex is TypeLoadException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static uint MaxSide => OcrEngine.MaxImageDimension;

    public static async Task<IReadOnlyList<string>> ReadLinesAsync(OcrEngine engine, RenderedPage page, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(page.Bmp.AsBuffer()).AsTask(cancellationToken);
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.BmpDecoderId, stream).AsTask(cancellationToken);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return result.Lines.Select(line => line.Text).Where(text => text.Length > 0).ToList();
    }
}

internal static class PdfErrors
{
    public static ConversionException PasswordProtected(string path, Exception? inner = null) =>
        new($"{Path.GetFileName(path)} is protected with a password. Open it, save a copy without the password, and try again.", inner);

    public static ConversionException Unreadable(string path, Exception? inner = null) =>
        new($"{Path.GetFileName(path)} couldn't be read. The file may be damaged, or it isn't really a PDF.", inner);
}
