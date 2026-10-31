using System.Runtime.InteropServices;
using System.Text;
using ImageMagick;
using Microsoft.CSharp.RuntimeBinder;
using Sulap.Core.Formats;
using Sulap.Core.Output;

namespace Sulap.Core.Conversion;

/// <summary>Documents the Microsoft Office programs open: PowerPoint, Excel, and Word.</summary>
internal static class OfficeConversions
{
    private const int MsoTrue = -1;
    private const int MsoFalse = 0;
    private const int PpSaveAsPdf = 32;
    private const int XlTypePdf = 0;
    private const int XlCsvUtf8 = 62;
    private const int XlSheetVisible = -1;
    private const int WdExportFormatPdf = 17;
    private const int WdFormatText = 2;
    private const int WdFormatDocumentDefault = 16;
    private const int WdStatisticPages = 2;
    private const int WdSeparateByTabs = 1;
    private const int Utf8CodePage = 65001;

    /// <summary>What a job produced: files in the job's output folder, and how many things they hold.</summary>
    private sealed record Exported(IReadOnlyList<(string Name, string Path)> Files, int Count, int Skipped);

    public static async Task<(ConversionOutput Output, IReadOnlyList<string> Notes)> ConvertAsync(
        string source, FileFormat input, FileFormat target, string folder, ConversionOptions options,
        Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        var app = input.OpensWith() ?? throw new ConversionException($"{Path.GetFileName(source)} isn't an Office document.");
        var baseName = OutputNames.BaseName(source);
        try
        {
            switch (app, target)
            {
                case (OfficeApp.PowerPoint, FileFormat.Pdf):
                    return await SaveSingleAsync(app, source, baseName, folder, target, "slide", options, report, PowerPointToPdf, cancellationToken);
                case (OfficeApp.PowerPoint, FileFormat.Jpeg or FileFormat.Png):
                    return await SaveSlidesAsync(source, baseName, folder, target, options, report, cancellationToken);
                case (OfficeApp.Excel, FileFormat.Pdf):
                    return await SaveSingleAsync(app, source, baseName, folder, target, "page", options, report, ExcelToPdf, cancellationToken);
                case (OfficeApp.Excel, FileFormat.Csv):
                    return await SaveSheetsAsync(source, baseName, folder, options, report, cancellationToken);
                case (OfficeApp.Word, FileFormat.Pdf):
                    return await SaveSingleAsync(app, source, baseName, folder, target, "page", options, report, WordToPdf, cancellationToken);
                case (OfficeApp.Word, FileFormat.Txt):
                    return await SaveSingleAsync(app, source, baseName, folder, target, "page", options, report, WordToText, cancellationToken);
                default:
                    throw new ConversionException($"{Path.GetFileName(source)} can't be converted to {target.Label()}.");
            }
        }
        catch (TimeoutException ex)
        {
            throw new ConversionException(
                $"{app.DisplayName()} took too long to convert {Path.GetFileName(source)}. Open the file in {app.DisplayName()} to see whether it needs attention.", ex);
        }
        catch (OfficeStoppedException ex)
        {
            throw new ConversionException($"{ex.Message}. Open {Path.GetFileName(source)} in {app.DisplayName()} to see what it asks.", ex);
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
            throw new ConversionException(
                $"{app.DisplayName()} couldn't convert {Path.GetFileName(source)}. The file may be password-protected or damaged.", ex);
        }
    }

    /// <summary>PDF → DOCX through Word's PDF Reflow, which keeps the layout far better than reading the text.</summary>
    public static async Task<string> PdfToDocxAsync(
        string source, string destination, ConversionOptions options, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        using var run = await OfficeAutomation.RunAsync(
            OfficeApp.Word,
            source,
            options.OfficeTimeout,
            report,
            context =>
            {
                var document = OpenWordDocument(context);
                try
                {
                    var docx = Path.Combine(context.OutputFolder, "converted.docx");
                    document.SaveAs2(FileName: docx, FileFormat: WdFormatDocumentDefault, AddToRecentFiles: false);
                    return docx;
                }
                finally
                {
                    Close(context, document);
                }
            },
            cancellationToken,
            OfficeAutomation.WordPdfReflowOption.Suppress);
        File.Copy(run.Result, destination);
        File.SetAttributes(destination, FileAttributes.Hidden); // Shown once it's moved into place.
        return destination;
    }

    // ---- The jobs that run inside the Office program ----
    // Every object a job gets from the program is passed through context.Track, so that all of them can
    // be released before the program is asked to quit; see OfficeContext.Track.

    private static Exported PowerPointToPdf(OfficeContext context)
    {
        var presentation = OpenPresentation(context);
        try
        {
            (int Visible, int Hidden) counts = CountSlides(context, presentation);
            var (visible, hidden) = counts;
            var pdf = Path.Combine(context.OutputFolder, "slides.pdf");
            context.Report(null, "Saving as PDF");
            presentation.SaveCopyAs(pdf, PpSaveAsPdf); // Hidden slides are left out of the PDF.
            return new Exported([("slides", pdf)], visible, hidden);
        }
        finally
        {
            Close(context, presentation);
        }
    }

    private static Exported PowerPointToImages(OfficeContext context, ConversionOptions options)
    {
        var presentation = OpenPresentation(context);
        try
        {
            dynamic slides = context.Track(presentation.Slides);
            int total = slides.Count;
            dynamic setup = context.Track(presentation.PageSetup);
            double widthPoints = setup.SlideWidth;
            double heightPoints = setup.SlideHeight;
            var scale = options.PdfDpi / 72.0;
            var (width, height) = (widthPoints * scale, heightPoints * scale);
            if (Math.Max(width, height) > PdfPageRenderer.MaxSide)
            {
                var shrink = PdfPageRenderer.MaxSide / Math.Max(width, height);
                (width, height) = (width * shrink, height * shrink);
            }

            var files = new List<(string Name, string Path)>();
            var hidden = 0;
            for (var number = 1; number <= total; number++)
            {
                context.ThrowIfStopped();
                dynamic slide = context.Track(slides.Item(number));
                if (IsHidden(context, slide))
                {
                    hidden++;
                    continue;
                }

                context.Report((double)(number - 1) / total, $"Slide {number} of {total}");
                var png = Path.Combine(context.OutputFolder, $"slide-{number}.png");
                slide.Export(png, "PNG", (int)Math.Round(width), (int)Math.Round(height));
                files.Add((number.ToString(System.Globalization.CultureInfo.InvariantCulture), png));
            }

            return files.Count == 0 ? throw new ConversionException(NoSlides()) : new Exported(files, files.Count, hidden);
        }
        finally
        {
            Close(context, presentation);
        }
    }

    private static Exported ExcelToPdf(OfficeContext context)
    {
        var workbook = OpenWorkbook(context);
        try
        {
            var pdf = Path.Combine(context.OutputFolder, "workbook.pdf");
            context.Report(null, "Saving as PDF");
            // Sheets that are hidden or empty are left out of the PDF.
            workbook.ExportAsFixedFormat(Type: XlTypePdf, Filename: pdf, Quality: 0, IncludeDocProperties: true, IgnorePrintAreas: false, OpenAfterPublish: false);
            return new Exported([("workbook", pdf)], 0, 0);
        }
        finally
        {
            Close(context, workbook);
        }
    }

    private static Exported ExcelToCsv(OfficeContext context)
    {
        var workbook = OpenWorkbook(context);
        try
        {
            dynamic sheets = context.Track(workbook.Worksheets);
            int total = sheets.Count;
            dynamic functions = context.Track(context.Application.WorksheetFunction);
            var files = new List<(string Name, string Path)>();
            var skipped = 0;
            for (var index = 1; index <= total; index++)
            {
                context.ThrowIfStopped();
                dynamic sheet = context.Track(sheets.Item(index));
                string name = sheet.Name;
                context.Report((double)(index - 1) / total, $"Sheet {index} of {total}");
                dynamic cells = context.Track(sheet.Cells);
                if ((int)sheet.Visible != XlSheetVisible || (double)functions.CountA(cells) == 0)
                {
                    skipped++; // Hidden, or nothing in it.
                    continue;
                }

                // A copy of the sheet in a workbook of its own: saving the workbook itself as CSV would
                // make it the CSV, and each CSV holds one sheet.
                sheet.Copy();
                dynamic copy = context.Track(context.Application.ActiveWorkbook);
                try
                {
                    var csv = Path.Combine(context.OutputFolder, $"sheet-{index}.csv");
                    copy.SaveAs(Filename: csv, FileFormat: XlCsvUtf8, Local: true);
                    files.Add((name, csv));
                }
                finally
                {
                    Close(context, copy);
                }
            }

            return files.Count == 0
                ? throw new ConversionException($"{Path.GetFileName(context.CopyPath)} has no data to save as CSV: every sheet is empty or hidden.")
                : new Exported(files, files.Count, skipped);
        }
        finally
        {
            Close(context, workbook);
        }
    }

    private static Exported WordToPdf(OfficeContext context)
    {
        var document = OpenWordDocument(context);
        try
        {
            var pdf = Path.Combine(context.OutputFolder, "document.pdf");
            context.Report(null, "Saving as PDF");
            document.ExportAsFixedFormat(
                OutputFileName: pdf, ExportFormat: WdExportFormatPdf, OpenAfterExport: false, OptimizeFor: 0, CreateBookmarks: 1, DocStructureTags: true);
            return new Exported([("document", pdf)], (int)document.ComputeStatistics(WdStatisticPages), 0);
        }
        finally
        {
            Close(context, document);
        }
    }

    private static Exported WordToText(OfficeContext context)
    {
        var document = OpenWordDocument(context);
        try
        {
            context.Report(null, "Saving as text");
            // Word writes a table cell's end as a bare line break; as tab-separated text, rows stay readable.
            dynamic tables = context.Track(document.Tables);
            for (int table = tables.Count; table >= 1; table--)
            {
                try
                {
                    context.Track(tables.Item(table)).ConvertToText(Separator: WdSeparateByTabs);
                }
                catch (Exception ex) when (ex is COMException or RuntimeBinderException)
                {
                    // A protected table stays as it is.
                }
            }

            var txt = Path.Combine(context.OutputFolder, "document.txt");
            document.SaveAs2(FileName: txt, FileFormat: WdFormatText, Encoding: Utf8CodePage, AddToRecentFiles: false);
            return new Exported([("document", txt)], (int)document.ComputeStatistics(WdStatisticPages), 0);
        }
        finally
        {
            Close(context, document);
        }
    }

    // ---- Opening and closing ----

    private static dynamic OpenPresentation(OfficeContext context)
    {
        try
        {
            // No window, read-only. The made-up password makes an encrypted file fail instead of asking.
            dynamic presentations = context.Track(context.Application.Presentations);
            return context.Track(presentations.Open(
                FileName: $"{context.CopyPath}::{OfficeAutomation.NoPassword}::", ReadOnly: MsoTrue, Untitled: MsoFalse, WithWindow: MsoFalse));
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
            throw CouldNotOpen(context, ex);
        }
    }

    private static dynamic OpenWorkbook(OfficeContext context)
    {
        try
        {
            dynamic workbooks = context.Track(context.Application.Workbooks);
            return context.Track(workbooks.Open(
                Filename: context.CopyPath, UpdateLinks: 0, ReadOnly: true, Password: OfficeAutomation.NoPassword,
                IgnoreReadOnlyRecommended: true, Notify: false, AddToMru: false));
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
            throw CouldNotOpen(context, ex);
        }
    }

    private static dynamic OpenWordDocument(OfficeContext context)
    {
        try
        {
            dynamic documents = context.Track(context.Application.Documents);
            return context.Track(documents.Open(
                FileName: context.CopyPath, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                PasswordDocument: OfficeAutomation.NoPassword, Visible: false, NoEncodingDialog: true));
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException)
        {
            throw CouldNotOpen(context, ex);
        }
    }

    private static ConversionException CouldNotOpen(OfficeContext context, Exception inner) =>
        new($"{Path.GetFileName(context.CopyPath)} couldn't be opened in {context.App.DisplayName()}. It may be password-protected or damaged.", inner);

    /// <summary>Closes the file without saving. PowerPoint's Close takes no arguments; Word and Excel are told not to save.</summary>
    private static void Close(OfficeContext context, dynamic document)
    {
        try
        {
            switch (context.App)
            {
                case OfficeApp.PowerPoint:
                    document.Close();
                    break;
                case OfficeApp.Word:
                    document.Close(SaveChanges: 0);
                    break;
                default:
                    document.Close(SaveChanges: false);
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or RuntimeBinderException or InvalidComObjectException)
        {
            // The program quits afterwards; a document that won't close must not hide the result.
        }
    }

    private static bool IsHidden(OfficeContext context, dynamic slide)
    {
        dynamic transition = context.Track(slide.SlideShowTransition);
        return (int)transition.Hidden == MsoTrue;
    }

    private static (int Visible, int Hidden) CountSlides(OfficeContext context, dynamic presentation)
    {
        dynamic slides = context.Track(presentation.Slides);
        int total = slides.Count;
        var hidden = 0;
        for (var number = 1; number <= total; number++)
        {
            if (IsHidden(context, context.Track(slides.Item(number))))
            {
                hidden++;
            }
        }

        return total - hidden == 0 ? throw new ConversionException(NoSlides()) : (total - hidden, hidden);
    }

    private static string NoSlides() => "The presentation has no slides to convert: every slide is hidden or there are none.";

    // ---- Saving the results next to the original ----

    /// <summary>One result file: a PDF, or a text file.</summary>
    private static async Task<(ConversionOutput, IReadOnlyList<string>)> SaveSingleAsync(
        OfficeApp app, string source, string baseName, string folder, FileFormat target, string unit, ConversionOptions options,
        Action<ConversionProgress> report, Func<OfficeContext, Exported> job, CancellationToken cancellationToken)
    {
        using var run = await StartAsync(app, source, options, report, job, cancellationToken);
        var exported = run.Result;
        var file = exported.Files[0].Path;
        var items = exported.Count;
        if (target == FileFormat.Pdf && (!File.Exists(file) || (items = PdfPages(file)) == 0))
        {
            throw new ConversionException($"{Path.GetFileName(source)} has nothing to print: it is empty, or all of it is hidden.");
        }

        var bytes = target == FileFormat.Txt ? Utf8WithBom(File.ReadAllBytes(file), fixLineBreaks: true) : null;
        var path = await OutputWriter.WriteFileAsync(
            folder,
            baseName,
            target.Extension(),
            async (stream, token) =>
            {
                if (bytes is not null)
                {
                    await stream.WriteAsync(bytes, token);
                    return;
                }

                await using var from = File.OpenRead(file);
                await from.CopyToAsync(stream, token);
            },
            cancellationToken);
        var notes = exported.Skipped > 0 ? new[] { HiddenNote(exported.Skipped, "slide") } : [];
        return (new ConversionOutput(path, IsFolder: false, new FileInfo(path).Length, Math.Max(items, 1), unit), notes);
    }

    /// <summary>One image for a one-slide presentation; a folder of slides otherwise.</summary>
    private static async Task<(ConversionOutput, IReadOnlyList<string>)> SaveSlidesAsync(
        string source, string baseName, string folder, FileFormat target, ConversionOptions options, Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        using var run = await StartAsync(OfficeApp.PowerPoint, source, options, report, context => PowerPointToImages(context, options), cancellationToken);
        var exported = run.Result;
        var notes = exported.Skipped > 0 ? new[] { HiddenNote(exported.Skipped, "slide") } : [];
        if (exported.Files.Count == 1)
        {
            var bytes = EncodeSlide(exported.Files[0].Path, target, options);
            var file = await OutputWriter.WriteFileAsync(
                folder, baseName, target.Extension(), (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken);
            return (new ConversionOutput(file, IsFolder: false, bytes.Length, Items: 1, "slide"), notes);
        }

        long total = 0;
        var slides = await OutputWriter.WriteFolderAsync(
            folder,
            OutputNames.PagesFolder(baseName, target),
            async (temp, token) =>
            {
                foreach (var (number, png) in exported.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var bytes = EncodeSlide(png, target, options);
                    await File.WriteAllBytesAsync(Path.Combine(temp, OutputNames.SlideFile(int.Parse(number, System.Globalization.CultureInfo.InvariantCulture), target)), bytes, token);
                    total += bytes.Length;
                }
            },
            cancellationToken);
        return (new ConversionOutput(slides, IsFolder: true, total, exported.Files.Count, "slide"), notes);
    }

    /// <summary>One CSV for a workbook with one sheet that has data; a folder with a CSV per sheet otherwise.</summary>
    private static async Task<(ConversionOutput, IReadOnlyList<string>)> SaveSheetsAsync(
        string source, string baseName, string folder, ConversionOptions options, Action<ConversionProgress> report, CancellationToken cancellationToken)
    {
        using var run = await StartAsync(OfficeApp.Excel, source, options, report, ExcelToCsv, cancellationToken);
        var exported = run.Result;
        var notes = exported.Skipped > 0 ? new[] { HiddenNote(exported.Skipped, "sheet", "is empty or hidden", "are empty or hidden") } : [];
        if (exported.Files.Count == 1)
        {
            var bytes = Utf8WithBom(File.ReadAllBytes(exported.Files[0].Path), fixLineBreaks: false);
            var file = await OutputWriter.WriteFileAsync(
                folder, baseName, FileFormat.Csv.Extension(), (stream, token) => stream.WriteAsync(bytes, token).AsTask(), cancellationToken);
            return (new ConversionOutput(file, IsFolder: false, bytes.Length, Items: 1, "sheet"), notes);
        }

        long total = 0;
        var sheets = await OutputWriter.WriteFolderAsync(
            folder,
            OutputNames.PagesFolder(baseName, FileFormat.Csv),
            async (temp, token) =>
            {
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, csv) in exported.Files)
                {
                    token.ThrowIfCancellationRequested();
                    var fileName = OutputNames.SheetFile(name, FileFormat.Csv);
                    for (var attempt = 2; !used.Add(fileName); attempt++)
                    {
                        fileName = OutputNames.SheetFile($"{name} ({attempt})", FileFormat.Csv);
                    }

                    var bytes = Utf8WithBom(File.ReadAllBytes(csv), fixLineBreaks: false);
                    await File.WriteAllBytesAsync(Path.Combine(temp, fileName), bytes, token);
                    total += bytes.Length;
                }
            },
            cancellationToken);
        return (new ConversionOutput(sheets, IsFolder: true, total, exported.Files.Count, "sheet"), notes);
    }

    private static async Task<OfficeRun<Exported>> StartAsync(
        OfficeApp app, string source, ConversionOptions options, Action<ConversionProgress> report, Func<OfficeContext, Exported> job,
        CancellationToken cancellationToken)
    {
        report(new ConversionProgress(null, $"Starting {app.DisplayName()}"));
        return await OfficeAutomation.RunAsync(app, source, options.OfficeTimeout, report, job, cancellationToken);
    }

    private static string HiddenNote(int count, string thing, string one = "is hidden", string many = "are hidden") =>
        count == 1 ? $"1 {thing} {one} and was left out." : $"{count} {thing}s {many} and were left out.";

    private static int PdfPages(string path)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        return pdf.NumberOfPages;
    }

    /// <summary>A slide as PNG (PowerPoint's own file) or JPG (flattened onto white, at the chosen quality).</summary>
    private static byte[] EncodeSlide(string pngPath, FileFormat target, ConversionOptions options)
    {
        if (target == FileFormat.Png)
        {
            return File.ReadAllBytes(pngPath);
        }

        using var image = new MagickImage(pngPath);
        ImageCodec.Prepare(image, FileFormat.Jpeg, srgbOnly: false);
        image.Density = new Density(options.PdfDpi, options.PdfDpi, DensityUnit.PixelsPerInch);
        image.Quality = (uint)Math.Clamp(options.Quality, 1, 100);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>
    /// UTF-8 with a byte order mark, so every Windows app recognizes it. Text gets Windows line breaks;
    /// a CSV keeps its own, because a line break inside a quoted cell is part of the cell.
    /// </summary>
    private static byte[] Utf8WithBom(byte[] bytes, bool fixLineBreaks)
    {
        var preamble = Encoding.UTF8.GetPreamble();
        var start = bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        var content = bytes.AsSpan(start);
        if (!fixLineBreaks)
        {
            return [.. preamble, .. content];
        }

        var text = Encoding.UTF8.GetString(content).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        return [.. preamble, .. Encoding.UTF8.GetBytes(text)];
    }
}
