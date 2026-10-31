using System.Globalization;
using ImageMagick;
using Sulap.Core;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;
using Sulap.Core.Formatting;

namespace Sulap.Cli;

/// <summary>The <c>sulap</c> command: a terminal front end to the engine in Sulap.Core.</summary>
internal static class CommandLine
{
    public const int Success = 0;
    public const int UsageError = 1;
    public const int Failure = 2;

    private static string Command => AppIdentity.Name.ToLowerInvariant();

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "-h" or "--help")
        {
            PrintUsage(output);
            return args.Length == 0 ? UsageError : Success;
        }

        switch (args[0])
        {
            case "--version" or "-v":
                output.WriteLine($"{Command} {AppIdentity.Version}");
                return Success;

            case "info" when args.Length == 2:
                return Info(args[1], output, error);

            case "convert" when args.Length > 1:
                return await ConvertAsync(args[1..], output, error);

            case "tool":
                error.WriteLine("'tool' isn't available yet: tools arrive in Phase 4.");
                return Failure;

            default:
                error.WriteLine($"Unknown command or wrong arguments: {string.Join(' ', args)}");
                PrintUsage(error);
                return UsageError;
        }
    }

    private static async Task<int> ConvertAsync(string[] args, TextWriter output, TextWriter error)
    {
        var files = new List<string>();
        FileFormat? target = null;
        string? outputFolder = null;
        var verbose = false;
        var options = new ConversionOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--to" when value is not null:
                    target = FileFormats.FromName(value);
                    if (target is null)
                    {
                        error.WriteLine($"Unknown format: {value}");
                        return UsageError;
                    }

                    i++;
                    break;
                case "--out" when value is not null:
                    outputFolder = Path.GetFullPath(value);
                    i++;
                    break;
                case "--dpi" when int.TryParse(value, CultureInfo.InvariantCulture, out var dpi) && dpi is >= 36 and <= 1200:
                    options = options with { PdfDpi = dpi };
                    i++;
                    break;
                case "--quality" when int.TryParse(value, CultureInfo.InvariantCulture, out var quality) && quality is >= 1 and <= 100:
                    options = options with { Quality = quality };
                    i++;
                    break;
                case "--no-word":
                    options = options with { UseWord = false };
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case var option when option.StartsWith("--", StringComparison.Ordinal):
                    error.WriteLine($"Unknown or incomplete option: {option}");
                    return UsageError;
                default:
                    files.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }

        if (target is null || files.Count == 0)
        {
            error.WriteLine($"Usage: {Command} convert <file>... --to <format> [--out <folder>] [--dpi <n>] [--quality <n>] [--no-word]");
            return UsageError;
        }

        var converter = new Converter(new FormatRegistry(EngineCapabilities.Detect()));
        var request = new ConversionRequest(files, target.Value) { OutputFolder = outputFolder, Options = options };
        var lastStatus = string.Empty;
        var progress = new Progress<ConversionProgress>(step =>
        {
            if (step.Status != lastStatus && step.Status is not ("Starting" or "Done"))
            {
                lastStatus = step.Status;
                error.WriteLine($"  {step.Status}…");
            }
        });

        var started = DateTime.Now;
        var result = await converter.ConvertAsync(request, progress);
        foreach (var made in result.Outputs)
        {
            var items = made.IsFolder || made.Items > 1 ? $", {made.ItemsText}" : string.Empty;
            output.WriteLine($"Saved {made.Path} ({FileSize.Format(made.Bytes)}{items})");
        }

        foreach (var note in result.Notes)
        {
            output.WriteLine($"Note: {note}");
        }

        foreach (var failure in result.Failures)
        {
            error.WriteLine($"Failed: {failure.Message}");
            if (verbose && failure.Detail is not null)
            {
                error.WriteLine(failure.Detail);
            }
        }

        if (result.NeedFolder.Count > 0)
        {
            error.WriteLine($"Can't save in {result.UnwritableFolder}. Choose another folder with --out <folder>.");
        }

        output.WriteLine($"Took {(DateTime.Now - started).TotalSeconds:0.0} s");
        return result.Succeeded ? Success : Failure;
    }

    private static int Info(string path, TextWriter output, TextWriter error)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            error.WriteLine($"File not found: {file.FullName}");
            return Failure;
        }

        output.WriteLine($"Name:      {file.Name}");
        output.WriteLine($"Folder:    {file.DirectoryName}");
        output.WriteLine($"Size:      {FileSize.Format(file.Length)} ({file.Length:N0} bytes)");
        output.WriteLine($"Modified:  {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
        output.WriteLine($"Extension: {(file.Extension.Length > 0 ? file.Extension : "(none)")}");

        var format = FileFormats.Detect(file.FullName);
        var byExtension = FileFormats.FromPath(file.FullName);
        var mismatch = format is not null && byExtension is not null && format != byExtension ? $" (the extension says {byExtension.Value.Label()})" : string.Empty;
        output.WriteLine($"Format:    {format?.Label() ?? "unknown"}{mismatch}");

        try
        {
            if (format == FileFormat.Pdf)
            {
                PdfDetails(file.FullName, output);
            }
            else if (format?.IsImage() == true)
            {
                ImageDetails(file.FullName, output);
            }
            else if (format == FileFormat.Docx)
            {
                output.WriteLine($"Contents:  {DocxSummary(file.FullName)}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Problem:   couldn't read the contents ({ex.GetType().Name}: {ex.Message})");
        }

        if (format is not null)
        {
            var options = new FormatRegistry(EngineCapabilities.Detect()).ConvertOptions(format.Value);
            var targets = options.Select(option => option.IsEnabled ? option.Label : $"{option.Label} (not available)");
            output.WriteLine($"Converts:  {(options.Count > 0 ? string.Join(", ", targets) : "nothing yet")}");
        }

        return Success;
    }

    private static void PdfDetails(string path, TextWriter output)
    {
        using var document = UglyToad.PdfPig.PdfDocument.Open(path);
        var withText = 0;
        foreach (var page in document.GetPages())
        {
            if (page.Letters.Any(letter => !string.IsNullOrWhiteSpace(letter.Value)))
            {
                withText++;
            }
        }

        var first = document.GetPage(1);
        output.WriteLine($"Pages:     {document.NumberOfPages} (first page {first.Width / 72 * 25.4:0} x {first.Height / 72 * 25.4:0} mm)");
        output.WriteLine($"Text:      {withText} of {document.NumberOfPages} pages have a text layer");
        output.WriteLine($"Encrypted: {(document.IsEncrypted ? "yes" : "no")}");
    }

    private static void ImageDetails(string path, TextWriter output)
    {
        using var image = new MagickImage();
        image.Ping(path);
        var exif = image.GetExifProfile();
        var gps = exif?.GetValue(ExifTag.GPSLatitude) is not null;
        var profile = image.GetColorProfile();
        output.WriteLine($"Pixels:    {image.Width} x {image.Height}, {image.ColorSpace}, {image.Depth}-bit{(image.HasAlpha ? ", with transparency" : string.Empty)}");
        output.WriteLine($"DPI:       {image.Density.X:0.#} x {image.Density.Y:0.#} {image.Density.Units}");
        output.WriteLine($"Rotation:  {image.Orientation}");
        output.WriteLine($"Profile:   {profile?.Description ?? profile?.Model ?? "none"}");
        output.WriteLine($"EXIF:      {(exif is null ? "none" : $"{exif.Values.Count} values{(gps ? ", with GPS location" : string.Empty)}")}");
    }

    private static string DocxSummary(string path)
    {
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(path, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;
        var paragraphs = body?.Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>().Count() ?? 0;
        var pictures = body?.Descendants<DocumentFormat.OpenXml.Drawing.Pictures.Picture>().Count() ?? 0;
        var words = body?.InnerText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length ?? 0;
        return $"{paragraphs} paragraphs, {words} words, {pictures} pictures";
    }

    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine($"{AppIdentity.Name} {AppIdentity.Version} — convert files from the terminal.");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine($"  {Command} info <file>                  Show what {AppIdentity.Name} knows about a file");
        writer.WriteLine($"  {Command} convert <file>... --to <fmt> Convert files, saving next to the originals");
        writer.WriteLine("        --out <folder>                    Save somewhere else");
        writer.WriteLine("        --dpi <n>                         Resolution for PDF pages as images (default 300)");
        writer.WriteLine("        --quality <n>                     JPG/WEBP/AVIF/HEIC quality, 1-100 (default 90)");
        writer.WriteLine("        --no-word                         PDF → DOCX without Microsoft Word (text only)");
        writer.WriteLine("        --verbose                         Show technical details when something fails");
        writer.WriteLine($"  {Command} tool <name> <file>           Run a tool such as compress (Phase 4)");
        writer.WriteLine($"  {Command} --version                    Print the version");
    }
}
