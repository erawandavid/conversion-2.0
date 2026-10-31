using DocumentFormat.OpenXml.Packaging;
using ImageMagick;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;

namespace Sulap.Core.Tests;

/// <summary>
/// Runs real conversions on files made in code. They use Windows engines (PDF rendering, OCR, the
/// HEIF encoder), so they skip themselves elsewhere. Word is only used when SULAP_TEST_WORD=1, so a
/// test run never starts Word by surprise.
/// </summary>
[Collection("Office")] // Two of them start Word; they must not overlap with the Office tests.
public sealed class ConverterTests : IDisposable
{
    private static readonly Lazy<EngineCapabilities> Detected = new(EngineCapabilities.Detect);

    private readonly TempFolder _folder = new();

    public ConverterTests()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Conversions use Windows engines.");
    }

    private static bool WordTestsEnabled => Environment.GetEnvironmentVariable("SULAP_TEST_WORD") == "1";

    private static EngineCapabilities Capabilities => WordTestsEnabled ? Detected.Value : Detected.Value with { Word = false };

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Photo_to_png_is_turned_upright_and_keeps_exif_and_gps()
    {
        var source = Fixtures.PhotoJpeg(_folder.File("Holiday.jpg"));

        var output = Assert.Single((await Convert(FileFormat.Png, source)).Outputs);

        Assert.Equal(_folder.File("Holiday.png"), output.Path);
        using var image = new MagickImage(output.Path);
        Assert.Equal((80u, 120u), (image.Width, image.Height));
        Assert.True(IsMarker(image, 70, 5), "The marker should be at the top right after turning the photo upright.");
        Assert.True(image.Orientation is Orientation.TopLeft or Orientation.Undefined);
        var exif = image.GetExifProfile();
        Assert.NotNull(exif);
        Assert.Equal("Fixture Camera", exif.GetValue(ExifTag.Model)?.Value);
        Assert.NotNull(exif.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public async Task Photo_to_webp_keeps_its_location()
    {
        var source = Fixtures.PhotoJpeg(_folder.File("Holiday.jpg"));

        var output = Assert.Single((await Convert(FileFormat.WebP, source)).Outputs);

        using var image = new MagickImage(output.Path);
        Assert.Equal(MagickFormat.WebP, image.Format);
        Assert.NotNull(image.GetExifProfile()?.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public async Task Transparency_becomes_white_in_jpg_and_stays_in_webp()
    {
        var source = Fixtures.AlphaPng(_folder.File("Logo.png"));

        var jpg = Assert.Single((await Convert(FileFormat.Jpeg, source)).Outputs);
        var webp = Assert.Single((await Convert(FileFormat.WebP, source)).Outputs);

        using (var image = new MagickImage(jpg.Path))
        {
            var right = image.GetPixels().GetPixel(56, 32).ToColor()!;
            Assert.True(right.R > 240 && right.G > 240 && right.B > 240, $"Expected white, got {right}");
        }

        using (var image = new MagickImage(webp.Path))
        {
            Assert.True(image.HasAlpha);
            Assert.True(image.GetPixels().GetPixel(56, 32).ToColor()!.A < 10);
        }
    }

    [Theory]
    [InlineData(FileFormat.Tiff, MagickFormat.Tiff)]
    [InlineData(FileFormat.Bmp, MagickFormat.Bmp)]
    [InlineData(FileFormat.Gif, MagickFormat.Gif)]
    [InlineData(FileFormat.Avif, MagickFormat.Avif)]
    public async Task Images_convert_to_each_format(FileFormat target, MagickFormat expected)
    {
        var source = Fixtures.PhotoJpeg(_folder.File("Photo.jpg"));

        var output = Assert.Single((await Convert(target, source)).Outputs);

        Assert.EndsWith(target.Extension(), output.Path);
        using var image = new MagickImage(output.Path);
        Assert.Equal(expected, image.Format);
        Assert.Equal((80u, 120u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Images_convert_to_heic()
    {
        Assert.SkipUnless(Detected.Value.CanWrite(FileFormat.Heic), "No HEIC encoder on this machine.");
        var source = Fixtures.PhotoJpeg(_folder.File("Photo.jpg"));

        var result = await Convert(FileFormat.Heic, source);

        // Name the encoder that was used and say why it failed, not just that nothing came out.
        Assert.True(
            result.Failures.Count == 0,
            $"Magick.NET writes HEIC: {Detected.Value.MagickWrites.Contains(FileFormat.Heic)}; Windows HEIF encoder: {Detected.Value.WindowsHeifEncoder}."
            + string.Concat(result.Failures.Select(failure => $"{Environment.NewLine}{failure.Message}{Environment.NewLine}{failure.Detail}")));
        var output = Assert.Single(result.Outputs);
        Assert.Equal(FileFormat.Heic, FileFormats.Detect(output.Path));
        using var image = new MagickImage(output.Path);
        Assert.Equal((80u, 120u), (image.Width, image.Height));
    }

    [Fact]
    public async Task Several_images_make_one_pdf_in_name_order()
    {
        Fixtures.SolidPng(_folder.File("Page 10.png"), 300, 100);
        Fixtures.SolidPng(_folder.File("Page 2.png"), 200, 100);
        var first = Fixtures.PhotoJpeg(_folder.File("Page 1.jpg"));

        var result = await Convert(FileFormat.Pdf, _folder.File("Page 10.png"), _folder.File("Page 2.png"), first);

        var output = Assert.Single(result.Outputs);
        Assert.Equal(_folder.File("Page 1.pdf"), output.Path);
        Assert.Equal(3, output.Items);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(output.Path);
        var widths = pdf.GetPages().Select(page => Math.Round(page.Width)).ToArray();
        // Page 1 is the upright photo (80 px at 96 dpi = 60 pt); the PNGs are 72 dpi, so pixels = points.
        Assert.Equal([60d, 200d, 300d], widths);
    }

    [Fact]
    public async Task An_image_becomes_a_word_document_with_the_picture()
    {
        var source = Fixtures.AlphaPng(_folder.File("Logo.png"));

        var output = Assert.Single((await Convert(FileFormat.Docx, source)).Outputs);

        using var document = WordprocessingDocument.Open(output.Path, isEditable: false);
        Assert.Single(document.MainDocumentPart!.ImageParts);
        Assert.Single(document.MainDocumentPart.Document!.Body!.Descendants<DocumentFormat.OpenXml.Drawing.Pictures.Picture>());
    }

    [Fact]
    public async Task A_pdf_becomes_text_page_by_page()
    {
        var source = Fixtures.TextPdf(_folder.File("Notes.pdf"), "First page\nsecond line", "Second page");

        var output = Assert.Single((await Convert(FileFormat.Txt, source)).Outputs);

        var text = File.ReadAllText(output.Path);
        Assert.Matches(@"First page\s+second line\s+Second page", text);
        Assert.Equal(2, output.Items);
    }

    [Fact]
    public async Task A_one_page_pdf_becomes_one_image_at_300_dpi()
    {
        var source = Fixtures.TextPdf(_folder.File("Letter.pdf"), "Hello");

        var output = Assert.Single((await Convert(FileFormat.Jpeg, source)).Outputs);

        Assert.Equal(_folder.File("Letter.jpg"), output.Path);
        using var image = new MagickImage(output.Path);
        Assert.Equal((2550u, 3300u), (image.Width, image.Height)); // 8.5 x 11 inches
        Assert.Equal(300, Math.Round(image.Density.X));
    }

    [Fact]
    public async Task A_longer_pdf_becomes_a_folder_of_pages()
    {
        var source = Fixtures.TextPdf(_folder.File("Report.pdf"), "One", "Two", "Three");

        var output = Assert.Single((await Convert(FileFormat.Png, source)).Outputs);

        Assert.True(output.IsFolder);
        Assert.Equal(_folder.File("Report (PNG)"), output.Path);
        Assert.Equal(["Page 1.png", "Page 2.png", "Page 3.png"], Directory.GetFiles(output.Path).Select(Path.GetFileName).Order());
        Assert.Equal(["Report (PNG)", "Report.pdf"], _folder.Names());
    }

    [Fact]
    public async Task Without_word_a_pdf_becomes_a_document_of_its_text()
    {
        var source = Fixtures.TextPdf(_folder.File("Notes.pdf"), "A heading line", "The second page");

        var result = await Convert(FileFormat.Docx, source, options: new ConversionOptions { UseWord = false });

        var output = Assert.Single(result.Outputs);
        using var document = WordprocessingDocument.Open(output.Path, isEditable: false);
        var text = document.MainDocumentPart!.Document!.Body!.InnerText;
        Assert.Contains("A heading line", text);
        Assert.Contains("The second page", text);
        Assert.Contains(result.Notes, note => note.Contains("without Microsoft Word"));
    }

    [Fact]
    public async Task With_word_a_pdf_becomes_a_document()
    {
        Assert.SkipUnless(WordTestsEnabled && Detected.Value.Word, "Set SULAP_TEST_WORD=1 on a machine with Word to run this.");
        var source = Fixtures.TextPdf(_folder.File("Notes.pdf"), "Converted by Word");

        var result = await Convert(FileFormat.Docx, source);

        var output = Assert.Single(result.Outputs);
        Assert.Empty(result.Notes);
        using var document = WordprocessingDocument.Open(output.Path, isEditable: false);
        Assert.Contains("Converted by Word", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    /// <remarks>Don't open Word yourself while this runs: it checks that no new Word is left running.</remarks>
    [Fact]
    public async Task When_word_takes_too_long_the_text_is_kept_and_its_word_is_closed()
    {
        Assert.SkipUnless(WordTestsEnabled && Detected.Value.Word, "Set SULAP_TEST_WORD=1 on a machine with Word to run this.");
        var source = Fixtures.TextPdf(_folder.File("Slow.pdf"), "Kept as text");
        var before = WordProcessIds();

        var result = await Convert(FileFormat.Docx, source, new ConversionOptions { OfficeTimeout = TimeSpan.FromSeconds(1) });

        var output = Assert.Single(result.Outputs);
        Assert.Contains(result.Notes, note => note.Contains("Word took too long", StringComparison.Ordinal));
        using (var document = WordprocessingDocument.Open(output.Path, isEditable: false))
        {
            Assert.Contains("Kept as text", document.MainDocumentPart!.Document!.Body!.InnerText);
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (WordProcessIds().Except(before).Any() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        Assert.Empty(WordProcessIds().Except(before));
        Assert.Equal(["Slow.docx", "Slow.pdf"], _folder.Names());
        Assert.Empty(Directory.GetDirectories(Path.GetTempPath(), $"{AppIdentity.Name}-word-*"));
        Assert.Empty(WordNotesAboutWorkingCopies());
    }

    /// <summary>Word's Resiliency entries that mention a working copy (they should all have been removed).</summary>
    private static List<string> WordNotesAboutWorkingCopies()
    {
        var found = new List<string>();
        using var resiliency = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Word\Resiliency");
        void Visit(Microsoft.Win32.RegistryKey key)
        {
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name) is byte[] data
                    && System.Text.Encoding.Unicode.GetString(data).Contains($@"\{AppIdentity.Name}-word-", StringComparison.OrdinalIgnoreCase))
                {
                    found.Add($@"{key.Name}\{name}");
                }
            }

            foreach (var child in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(child);
                if (sub is not null)
                {
                    Visit(sub);
                }
            }
        }

        if (resiliency is not null)
        {
            Visit(resiliency);
        }

        return found;
    }

    private static HashSet<int> WordProcessIds()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName("WINWORD");
        var ids = processes.Select(process => process.Id).ToHashSet();
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return ids;
    }

    [Fact]
    public async Task Scanned_pages_are_read_with_ocr()
    {
        Assert.SkipWhen(PageOcr.CreateEngine() is null, "Windows has no OCR language for this user.");
        var picture = Fixtures.TextImage(_folder.File("Scan.png"), "HELLO SULAP");
        var pdf = Assert.Single((await Convert(FileFormat.Pdf, picture)).Outputs);

        var result = await Convert(FileFormat.Txt, pdf.Path);

        var text = File.ReadAllText(Assert.Single(result.Outputs).Path);
        Assert.Contains("HELLO", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Notes, note => note.Contains("OCR"));
    }

    [Fact]
    public async Task Converting_twice_never_overwrites()
    {
        var source = Fixtures.AlphaPng(_folder.File("Logo.png"));

        await Convert(FileFormat.Jpeg, source);
        var second = Assert.Single((await Convert(FileFormat.Jpeg, source)).Outputs);

        Assert.Equal(_folder.File("Logo (2).jpg"), second.Path);
        Assert.Equal(["Logo (2).jpg", "Logo.jpg", "Logo.png"], _folder.Names());
    }

    [Fact]
    public async Task Problems_are_explained_in_plain_words()
    {
        var locked = Fixtures.PasswordPdf(_folder.File("Locked.pdf"));
        var broken = _folder.File("Broken.jpg");
        File.WriteAllText(broken, "this is not a picture");
        var zip = _folder.File("Archive.zip");
        File.WriteAllText(zip, "PK");
        var png = Fixtures.AlphaPng(_folder.File("Logo.png"));

        var toText = await Convert(FileFormat.Txt, locked);
        var toPng = await Convert(FileFormat.Png, broken, zip, png);

        Assert.Contains("password", Assert.Single(toText.Failures).Message);
        Assert.Equal(3, toPng.Failures.Count);
        Assert.Contains(toPng.Failures, failure => failure.Message.Contains("Broken.jpg couldn't be read"));
        Assert.Contains(toPng.Failures, failure => failure.Message.Contains("Archive.zip"));
        Assert.Contains(toPng.Failures, failure => failure.Message == "Logo.png is already a PNG file.");
        Assert.Empty(toPng.Outputs);
        Assert.Equal(["Archive.zip", "Broken.jpg", "Locked.pdf", "Logo.png"], _folder.Names());
    }

    [Fact]
    public async Task One_bad_file_doesnt_stop_the_others()
    {
        var good = Fixtures.AlphaPng(_folder.File("Good.png"));
        var bad = _folder.File("Bad.png");
        File.WriteAllText(bad, "nope");

        var result = await Convert(FileFormat.Jpeg, bad, good);

        Assert.Single(result.Failures);
        Assert.Equal(_folder.File("Good.jpg"), Assert.Single(result.Outputs).Path);
    }

    [Fact]
    public async Task An_output_folder_can_be_chosen()
    {
        var source = Fixtures.AlphaPng(_folder.File("Logo.png"));
        var elsewhere = Directory.CreateDirectory(_folder.File("elsewhere")).FullName;

        var result = await Convert(FileFormat.Jpeg, source, outputFolder: elsewhere);

        Assert.Equal(Path.Combine(elsewhere, "Logo.jpg"), Assert.Single(result.Outputs).Path);
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        var source = Fixtures.TextPdf(_folder.File("Report.pdf"), "One", "Two");
        var steps = new List<ConversionProgress>();

        await new Converter(new FormatRegistry(Capabilities)).ConvertAsync(
            new ConversionRequest([source], FileFormat.Png), new SynchronousProgress(steps.Add), TestContext.Current.CancellationToken);

        Assert.Contains(steps, step => step.Status == "Page 2 of 2");
        Assert.Equal(1, steps[^1].Fraction);
    }

    private static bool IsMarker(MagickImage image, int x, int y)
    {
        var color = image.GetPixels().GetPixel(x, y).ToColor()!;
        return color.R > 200 && color.G < 80 && color.B < 80;
    }

    private Task<ConversionResult> Convert(FileFormat target, params string[] sources) => Convert(target, sources, null, null);

    private Task<ConversionResult> Convert(FileFormat target, string source, ConversionOptions? options = null, string? outputFolder = null) =>
        Convert(target, [source], options, outputFolder);

    private static Task<ConversionResult> Convert(FileFormat target, string[] sources, ConversionOptions? options, string? outputFolder) =>
        new Converter(new FormatRegistry(Capabilities)).ConvertAsync(
            new ConversionRequest(sources, target) { Options = options ?? new ConversionOptions(), OutputFolder = outputFolder },
            progress: null,
            TestContext.Current.CancellationToken);

    /// <summary>Progress&lt;T&gt; posts to a thread pool; tests need the reports in order.</summary>
    private sealed class SynchronousProgress(Action<ConversionProgress> report) : IProgress<ConversionProgress>
    {
        public void Report(ConversionProgress value) => report(value);
    }
}
