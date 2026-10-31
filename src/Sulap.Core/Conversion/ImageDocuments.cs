using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Sulap.Core.Formats;
using Sulap.Core.Output;

namespace Sulap.Core.Conversion;

/// <summary>Images into documents: one PDF with a page per image, or a DOCX holding the image.</summary>
internal static class ImageDocuments
{
    /// <summary>The PDF format's practical page size limit (200 inches), in points.</summary>
    private const double MaxPagePoints = 14400;

    /// <summary>
    /// One PDF with every image as a page, in the given order. Each page is the size of its image at the
    /// image's resolution, so nothing is scaled or compressed again.
    /// </summary>
    public static async Task<ConversionOutput> ToPdfAsync(
        IReadOnlyList<(string Path, FileFormat Format)> images, string folder, string baseName, Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        using var document = new PdfDocument();
        document.Info.Title = baseName;
        document.Info.Creator = AppIdentity.Name;
        var drawn = new List<XImage>();
        try
        {
            for (var i = 0; i < images.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report(new ConversionProgress((double)i / images.Count, images.Count > 1 ? $"Image {i + 1} of {images.Count}" : "Converting"));
                foreach (var image in ImageCodec.ForEmbedding(images[i].Path, images[i].Format))
                {
                    var (width, height) = PageSize(image);
                    var page = document.AddPage();
                    page.Width = XUnit.FromPoint(width);
                    page.Height = XUnit.FromPoint(height);
                    var picture = XImage.FromStream(new MemoryStream(image.Bytes, writable: false));
                    drawn.Add(picture);
                    using var graphics = XGraphics.FromPdfPage(page);
                    graphics.DrawImage(picture, 0, 0, width, height);
                }
            }

            // PDFsharp locks the document once it's saved, so count the pages first.
            var pageCount = document.PageCount;
            var path = await OutputWriter.WriteFileAsync(
                folder,
                baseName,
                FileFormat.Pdf.Extension(),
                (stream, _) =>
                {
                    document.Save(stream, closeStream: false);
                    return Task.CompletedTask;
                },
                cancellationToken);
            return new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, pageCount);
        }
        finally
        {
            foreach (var picture in drawn)
            {
                picture.Dispose();
            }
        }
    }

    /// <summary>A Word document with the image on its first page.</summary>
    public static async Task<ConversionOutput> ToDocxAsync(string source, FileFormat format, string folder, CancellationToken cancellationToken)
    {
        var image = ImageCodec.ForEmbedding(source, format)[0];
        var baseName = OutputNames.BaseName(source);
        var path = await OutputWriter.WriteFileAsync(
            folder,
            baseName,
            FileFormat.Docx.Extension(),
            (stream, _) =>
            {
                DocxWriter.WriteImage(stream, image, baseName);
                return Task.CompletedTask;
            },
            cancellationToken);
        return new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, Items: 1);
    }

    private static (double Width, double Height) PageSize(EmbeddedImage image)
    {
        var width = image.Width * 72 / image.DpiX;
        var height = image.Height * 72 / image.DpiY;
        var scale = Math.Min(1, MaxPagePoints / Math.Max(width, height));
        return (Math.Max(1, width * scale), Math.Max(1, height * scale));
    }
}
