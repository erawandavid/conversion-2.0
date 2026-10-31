using System.Text;
using ImageMagick;
using Sulap.Core.Formats;
using Sulap.Core.Output;

namespace Sulap.Core.Conversion;

/// <summary>PDF → images, text, and Word documents.</summary>
internal static class PdfConversions
{
    /// <summary>
    /// One image for a one-page PDF ("Report.jpg"); a folder of pages otherwise ("Report (JPG)\Page 1.jpg").
    /// </summary>
    public static async Task<ConversionOutput> ToImagesAsync(
        string source, FileFormat target, string folder, ConversionOptions options, Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        using var renderer = await PdfPageRenderer.OpenAsync(source, cancellationToken);
        var count = renderer.PageCount;
        if (count == 0)
        {
            throw new ConversionException($"{Path.GetFileName(source)} has no pages.");
        }

        var baseName = OutputNames.BaseName(source);
        if (count == 1)
        {
            report(new ConversionProgress(null, "Page 1 of 1"));
            var bytes = await RenderPageAsync(renderer, 0, target, options, cancellationToken);
            var file = await OutputWriter.WriteFileAsync(
                folder, baseName, target.Extension(), (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken);
            return new ConversionOutput(file, IsFolder: false, bytes.Length, Items: 1);
        }

        long total = 0;
        var pagesFolder = await OutputWriter.WriteFolderAsync(
            folder,
            OutputNames.PagesFolder(baseName, target),
            async (temp, token) =>
            {
                for (var i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    report(new ConversionProgress((double)i / count, $"Page {i + 1} of {count}"));
                    var bytes = await RenderPageAsync(renderer, i, target, options, token);
                    await File.WriteAllBytesAsync(Path.Combine(temp, OutputNames.PageFile(i + 1, target)), bytes, token);
                    total += bytes.Length;
                }
            },
            cancellationToken);
        return new ConversionOutput(pagesFolder, IsFolder: true, total, count);
    }

    /// <summary>The text layer of every page (OCR for scanned pages), pages separated by a blank line.</summary>
    public static async Task<(ConversionOutput Output, IReadOnlyList<string> Notes)> ToTextAsync(
        string source, string folder, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        var text = await PdfText.ReadAsync(source, paragraphs: false, report, cancellationToken);
        var content = string.Join("\r\n\r\n", text.Pages.Select(page => page.Text)) + "\r\n";
        var notes = text.Notes.ToList();
        if (text.Pages.All(page => page.Text.Length == 0))
        {
            notes.Insert(0, "The PDF has no text that could be read, so the TXT file is empty.");
        }

        var path = await OutputWriter.WriteFileAsync(
            folder,
            OutputNames.BaseName(source),
            FileFormat.Txt.Extension(),
            async (stream, token) =>
            {
                // With a byte order mark, every Windows app recognizes the file as UTF-8.
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble()
                    .Concat(Encoding.UTF8.GetBytes(content)).ToArray();
                await stream.WriteAsync(bytes, token);
            },
            cancellationToken);
        return (new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, text.Pages.Count), notes);
    }

    /// <summary>
    /// Word's PDF Reflow when Word is available. Otherwise, or when Word fails, the text in
    /// paragraphs (text first: the layout isn't kept).
    /// </summary>
    public static async Task<(ConversionOutput Output, IReadOnlyList<string> Notes)> ToDocxAsync(
        string source, string folder, ConversionOptions options, EngineCapabilities capabilities, Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var baseName = OutputNames.BaseName(source);
        string? wordProblem = null;
        if (options.UseWord && capabilities.Word)
        {
            report(new ConversionProgress(null, "Converting with Microsoft Word"));
            try
            {
                var path = await OutputWriter.WriteFileByPathAsync(
                    folder,
                    baseName,
                    FileFormat.Docx.Extension(),
                    (temp, token) => OfficeConversions.PdfToDocxAsync(source, temp, options, report, token),
                    cancellationToken);
                return (new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, Items: 1), []);
            }
            catch (OutputFolderNotWritableException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                wordProblem = "Word took too long";
            }
            catch (OfficeStoppedException)
            {
                wordProblem = "Word stopped to show a message";
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                wordProblem = "Word couldn't convert it";
            }
        }

        var text = await PdfText.ReadAsync(source, paragraphs: true, report, cancellationToken);
        var docx = await OutputWriter.WriteFileAsync(
            folder,
            baseName,
            FileFormat.Docx.Extension(),
            (stream, _) =>
            {
                DocxWriter.WriteText(stream, text.Pages, baseName);
                return Task.CompletedTask;
            },
            cancellationToken);

        var notes = new List<string>
        {
            wordProblem is null
                ? "Made without Microsoft Word: the text is kept, the layout isn't."
                : $"{wordProblem}, so only the text was kept, not the layout.",
        };
        notes.AddRange(text.Notes);
        return (new ConversionOutput(docx, IsFolder: false, new FileInfo(docx).Length, text.Pages.Count), notes);
    }

    private static async Task<byte[]> RenderPageAsync(
        PdfPageRenderer renderer, int pageIndex, FileFormat target, ConversionOptions options, CancellationToken cancellationToken)
    {
        var page = await renderer.RenderAsync(pageIndex, options.PdfDpi, PdfPageRenderer.MaxSide, cancellationToken);
        using var image = new MagickImage(page.Bmp, MagickFormat.Bmp);
        // The page was drawn on white; the alpha channel carries nothing.
        image.Alpha(AlphaAction.Off);
        image.Density = new Density(page.Dpi, page.Dpi, DensityUnit.PixelsPerInch);
        if (target == FileFormat.Jpeg)
        {
            image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
        }

        return image.ToByteArray(ImageCodec.ToMagickFormat(target)!.Value);
    }
}
