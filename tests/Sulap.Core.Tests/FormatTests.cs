using Sulap.Core.Formats;
using Sulap.Core.Formatting;

namespace Sulap.Core.Tests;

public sealed class FileFormatTests
{
    [Theory]
    [InlineData("Report.pdf", FileFormat.Pdf)]
    [InlineData("photo.JPEG", FileFormat.Jpeg)]
    [InlineData("photo.jpg", FileFormat.Jpeg)]
    [InlineData("IMG_0001.HEIC", FileFormat.Heic)]
    [InlineData("scan.tif", FileFormat.Tiff)]
    [InlineData("C:\\folder.with.dots\\notes.txt", FileFormat.Txt)]
    public void Extensions_name_their_format(string path, FileFormat expected)
    {
        Assert.Equal(expected, FileFormats.FromPath(path));
    }

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("README")]
    [InlineData("C:\\Some Folder")]
    public void Unknown_extensions_have_no_format(string path)
    {
        Assert.Null(FileFormats.FromPath(path));
    }

    [Theory]
    [InlineData("jpg", FileFormat.Jpeg)]
    [InlineData("JPEG", FileFormat.Jpeg)]
    [InlineData(".png", FileFormat.Png)]
    [InlineData(" webp ", FileFormat.WebP)]
    public void Typed_names_are_understood(string name, FileFormat expected)
    {
        Assert.Equal(expected, FileFormats.FromName(name));
    }

    [Fact]
    public void Labels_and_extensions_match_the_wheel()
    {
        Assert.Equal("JPG", FileFormat.Jpeg.Label());
        Assert.Equal(".jpg", FileFormat.Jpeg.Extension());
        Assert.Equal(".tiff", FileFormat.Tiff.Extension());
        Assert.Equal("DOCX", FileFormat.Docx.Label());
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F' }, FileFormat.Jpeg)]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0 }, FileFormat.Png)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0 }, FileFormat.Gif)]
    [InlineData(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 }, FileFormat.Tiff)]
    [InlineData(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8 }, FileFormat.Tiff)]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, FileFormat.WebP)]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0x46, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0 }, FileFormat.Bmp)]
    public void Content_is_recognized(byte[] header, FileFormat expected)
    {
        Assert.Equal(expected, FileFormats.FromContent(header));
    }

    [Fact]
    public void A_pdf_header_after_junk_is_still_a_pdf()
    {
        var header = "\r\n\r\n%PDF-1.7\n%"u8.ToArray();

        Assert.Equal(FileFormat.Pdf, FileFormats.FromContent(header));
    }

    [Theory]
    [InlineData("heic", "mif1heic", FileFormat.Heic)]
    [InlineData("mif1", "mif1heic", FileFormat.Heic)]
    [InlineData("avif", "mif1miaf", FileFormat.Avif)]
    [InlineData("mif1", "avifmiaf", FileFormat.Avif)]
    public void Heif_brands_tell_heic_from_avif(string majorBrand, string compatibleBrands, FileFormat expected)
    {
        Assert.Equal(expected, FileFormats.FromContent(Ftyp(majorBrand, compatibleBrands)));
    }

    [Fact]
    public void A_video_container_is_not_an_image()
    {
        Assert.Null(FileFormats.FromContent(Ftyp("isom", "iso2mp41")));
    }

    [Fact]
    public void Detect_prefers_the_content_over_the_extension()
    {
        using var folder = new TempFolder();
        var path = folder.File("really-a-png.jpg");
        File.WriteAllBytes(path, [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);

        Assert.Equal(FileFormat.Png, FileFormats.Detect(path));
    }

    [Fact]
    public void Natural_order_sorts_numbers_by_value()
    {
        string[] names = ["Page 10.png", "page 2.png", "Page 1.jpg", "Scan.png"];

        Assert.Equal(["Page 1.jpg", "page 2.png", "Page 10.png", "Scan.png"], names.Order(NaturalOrder.FileNames));
    }

    /// <summary>An ISO media ftyp box: size, "ftyp", major brand, minor version, compatible brands.</summary>
    private static byte[] Ftyp(string majorBrand, string compatibleBrands)
    {
        var body = System.Text.Encoding.ASCII.GetBytes("ftyp" + majorBrand + "\0\0\0\0" + compatibleBrands);
        var size = body.Length + 4;
        return [0, 0, 0, (byte)size, .. body];
    }
}

public sealed class FormatRegistryTests
{
    private readonly FormatRegistry _registry = new(EngineCapabilities.All);

    [Fact]
    public void A_pdf_wheel_has_docx_top_jpg_right_png_bottom_txt_left()
    {
        Assert.Equal(["DOCX", "JPG", "PNG", "TXT"], Labels(_registry.ConvertOptions(FileFormat.Pdf)));
    }

    [Fact]
    public void A_jpg_wheel_has_eight_wedges()
    {
        Assert.Equal(["PNG", "WEBP", "HEIC", "TIFF", "AVIF", "BMP", "PDF", "DOCX"], Labels(_registry.ConvertOptions(FileFormat.Jpeg)));
    }

    [Theory]
    [InlineData(FileFormat.Png)]
    [InlineData(FileFormat.WebP)]
    [InlineData(FileFormat.Heic)]
    [InlineData(FileFormat.Tiff)]
    [InlineData(FileFormat.Gif)]
    public void Every_image_offers_jpg_and_never_itself(FileFormat input)
    {
        var options = _registry.ConvertOptions(input);

        Assert.Contains(options, option => option.Target == FileFormat.Jpeg);
        Assert.DoesNotContain(options, option => option.Target == input);
        Assert.InRange(options.Count, 1, FormatRegistry.MaxWedges);
    }

    [Fact]
    public void Heic_without_an_encoder_is_shown_disabled_with_a_hint()
    {
        var registry = new FormatRegistry(EngineCapabilities.All with
        {
            MagickWrites = EngineCapabilities.All.MagickWrites.Where(format => format != FileFormat.Heic).ToHashSet(),
            WindowsHeifEncoder = false,
        });

        var heic = Assert.Single(registry.ConvertOptions(FileFormat.Jpeg), option => option.Target == FileFormat.Heic);

        Assert.False(heic.IsEnabled);
        Assert.Contains("HEIF", heic.Hint);
        Assert.False(registry.CanConvert(FileFormat.Jpeg, FileFormat.Heic));
    }

    [Fact]
    public void Formats_nothing_can_write_are_left_out()
    {
        var registry = new FormatRegistry(EngineCapabilities.All with
        {
            MagickWrites = EngineCapabilities.All.MagickWrites.Where(format => format != FileFormat.Avif).ToHashSet(),
        });

        Assert.DoesNotContain(registry.ConvertOptions(FileFormat.Png), option => option.Target == FileFormat.Avif);
    }

    [Fact]
    public void Several_images_get_the_options_they_share()
    {
        var options = _registry.ConvertOptions([FileFormat.Jpeg, FileFormat.Png]);

        Assert.Equal(["WEBP", "HEIC", "TIFF", "AVIF", "BMP", "PDF", "DOCX", "GIF"], Labels(options));
    }

    [Fact]
    public void Gif_falls_off_a_full_wheel_but_the_engine_still_makes_it()
    {
        Assert.DoesNotContain(_registry.ConvertOptions(FileFormat.Jpeg), option => option.Target == FileFormat.Gif);
        Assert.True(_registry.CanConvert(FileFormat.Jpeg, FileFormat.Gif));
        Assert.False(_registry.CanConvert(FileFormat.Jpeg, FileFormat.Jpeg));
        Assert.False(_registry.CanConvert(FileFormat.Pdf, FileFormat.WebP));
    }

    [Fact]
    public void Several_files_of_one_type_get_its_wheel()
    {
        Assert.Equal(_registry.ConvertOptions(FileFormat.Pdf), _registry.ConvertOptions([FileFormat.Pdf, FileFormat.Pdf]));
    }

    [Fact]
    public void A_pdf_and_an_image_share_png_and_docx()
    {
        Assert.Equal(["DOCX", "PNG"], Labels(_registry.ConvertOptions([FileFormat.Pdf, FileFormat.Jpeg])));
    }

    [Fact]
    public void Text_and_csv_files_have_no_options_yet()
    {
        Assert.Empty(_registry.ConvertOptions(FileFormat.Txt));
        Assert.Empty(_registry.ConvertOptions(FileFormat.Csv));
        Assert.Empty(_registry.ConvertOptions([FileFormat.Txt, FileFormat.Csv]));
    }

    [Theory]
    [InlineData(FileFormat.Pptx, new[] { "PDF", "JPG", "PNG" })]
    [InlineData(FileFormat.Ppt, new[] { "PDF", "JPG", "PNG" })]
    [InlineData(FileFormat.Xlsx, new[] { "PDF", "CSV" })]
    [InlineData(FileFormat.Xls, new[] { "PDF", "CSV" })]
    [InlineData(FileFormat.Docx, new[] { "PDF", "TXT" })]
    [InlineData(FileFormat.Doc, new[] { "PDF", "TXT" })]
    public void Office_documents_have_their_own_wheels(FileFormat input, string[] expected)
    {
        Assert.Equal(expected, Labels(_registry.ConvertOptions(input)));
    }

    [Fact]
    public void Without_the_office_program_the_wedges_stay_but_are_disabled_with_a_hint()
    {
        var registry = new FormatRegistry(EngineCapabilities.All with { PowerPoint = false });

        var options = registry.ConvertOptions(FileFormat.Pptx);

        Assert.Equal(["PDF", "JPG", "PNG"], Labels(options));
        Assert.All(options, option => Assert.False(option.IsEnabled));
        Assert.All(options, option => Assert.Equal("Needs Microsoft PowerPoint.", option.Hint));
        Assert.False(registry.CanConvert(FileFormat.Pptx, FileFormat.Pdf));
        Assert.Contains("needs Microsoft PowerPoint", registry.WhyNot(FileFormat.Pptx, FileFormat.Pdf));
        Assert.All(registry.ConvertOptions(FileFormat.Xlsx), option => Assert.True(option.IsEnabled));
    }

    [Fact]
    public void Office_documents_are_never_offered_to_become_themselves_or_unknown_things()
    {
        Assert.False(_registry.CanConvert(FileFormat.Pptx, FileFormat.Pptx));
        Assert.False(_registry.CanConvert(FileFormat.Xlsx, FileFormat.Txt));
        Assert.False(_registry.CanConvert(FileFormat.Docx, FileFormat.Csv));
        Assert.True(_registry.CanConvert(FileFormat.Xlsx, FileFormat.Csv));
        Assert.Null(_registry.WhyNot(FileFormat.Xlsx, FileFormat.Txt));
    }

    [Fact]
    public void Office_documents_and_other_files_share_only_what_they_all_can_become()
    {
        Assert.Equal(["PDF"], Labels(_registry.ConvertOptions([FileFormat.Jpeg, FileFormat.Docx])));
        Assert.Equal(["JPG", "PNG"], Labels(_registry.ConvertOptions([FileFormat.Pdf, FileFormat.Pptx])));
        Assert.Equal(["PDF"], Labels(_registry.ConvertOptions([FileFormat.Pptx, FileFormat.Xlsx, FileFormat.Docx])));
        Assert.Equal(Labels(_registry.ConvertOptions([FileFormat.Docx, FileFormat.Jpeg])), Labels(_registry.ConvertOptions([FileFormat.Jpeg, FileFormat.Docx])));
    }

    [Fact]
    public void One_document_without_its_program_turns_off_the_shared_wedge_for_all()
    {
        var registry = new FormatRegistry(EngineCapabilities.All with { Word = false });

        var pdf = Assert.Single(registry.ConvertOptions([FileFormat.Jpeg, FileFormat.Docx]));

        Assert.False(pdf.IsEnabled);
        Assert.Equal("Needs Microsoft Word.", pdf.Hint);
    }

    [Theory]
    [InlineData("Deck.pptx", FileFormat.Pptx)]
    [InlineData("Show.PPSX", FileFormat.Pptx)]
    [InlineData("Old.ppt", FileFormat.Ppt)]
    [InlineData("Budget.xlsx", FileFormat.Xlsx)]
    [InlineData("Macros.xlsm", FileFormat.Xlsx)]
    [InlineData("Old.xls", FileFormat.Xls)]
    [InlineData("Letter.docx", FileFormat.Docx)]
    [InlineData("Old.doc", FileFormat.Doc)]
    [InlineData("data.csv", FileFormat.Csv)]
    public void Office_extensions_name_their_format(string path, FileFormat expected)
    {
        Assert.Equal(expected, FileFormats.FromPath(path));
    }

    private static string[] Labels(IEnumerable<ConvertOption> options) => [.. options.Select(option => option.Label)];
}
