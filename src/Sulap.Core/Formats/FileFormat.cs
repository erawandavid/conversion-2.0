using System.Text;

namespace Sulap.Core.Formats;

/// <summary>The file types the engine reads or writes.</summary>
public enum FileFormat
{
    Pdf,
    Jpeg,
    Png,
    WebP,
    Heic,
    Avif,
    Tiff,
    Bmp,
    Gif,
    Docx,
    Txt,

    /// <summary>PowerPoint presentations: .pptx and its macro-enabled and slide show variants.</summary>
    Pptx,

    /// <summary>Old binary PowerPoint presentations: .ppt, .pps.</summary>
    Ppt,

    /// <summary>Excel workbooks: .xlsx and the macro-enabled .xlsm.</summary>
    Xlsx,

    /// <summary>Old binary Excel workbooks: .xls.</summary>
    Xls,

    /// <summary>Old binary Word documents: .doc.</summary>
    Doc,

    Csv,
}

public static class FileFormats
{
    private static readonly Dictionary<string, FileFormat> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = FileFormat.Pdf,
        [".jpg"] = FileFormat.Jpeg,
        [".jpeg"] = FileFormat.Jpeg,
        [".jpe"] = FileFormat.Jpeg,
        [".jfif"] = FileFormat.Jpeg,
        [".png"] = FileFormat.Png,
        [".webp"] = FileFormat.WebP,
        [".heic"] = FileFormat.Heic,
        [".heif"] = FileFormat.Heic,
        [".hif"] = FileFormat.Heic,
        [".avif"] = FileFormat.Avif,
        [".tif"] = FileFormat.Tiff,
        [".tiff"] = FileFormat.Tiff,
        [".bmp"] = FileFormat.Bmp,
        [".dib"] = FileFormat.Bmp,
        [".gif"] = FileFormat.Gif,
        [".docx"] = FileFormat.Docx,
        [".docm"] = FileFormat.Docx,
        [".doc"] = FileFormat.Doc,
        [".pptx"] = FileFormat.Pptx,
        [".pptm"] = FileFormat.Pptx,
        [".ppsx"] = FileFormat.Pptx,
        [".ppt"] = FileFormat.Ppt,
        [".pps"] = FileFormat.Ppt,
        [".xlsx"] = FileFormat.Xlsx,
        [".xlsm"] = FileFormat.Xlsx,
        [".xls"] = FileFormat.Xls,
        [".csv"] = FileFormat.Csv,
        [".txt"] = FileFormat.Txt,
    };

    /// <summary>How a format is shown on a wedge and in messages: "JPG", "PDF".</summary>
    public static string Label(this FileFormat format) => format switch
    {
        FileFormat.Pdf => "PDF",
        FileFormat.Jpeg => "JPG",
        FileFormat.Png => "PNG",
        FileFormat.WebP => "WEBP",
        FileFormat.Heic => "HEIC",
        FileFormat.Avif => "AVIF",
        FileFormat.Tiff => "TIFF",
        FileFormat.Bmp => "BMP",
        FileFormat.Gif => "GIF",
        FileFormat.Docx => "DOCX",
        FileFormat.Txt => "TXT",
        FileFormat.Pptx => "PPTX",
        FileFormat.Ppt => "PPT",
        FileFormat.Xlsx => "XLSX",
        FileFormat.Xls => "XLS",
        FileFormat.Doc => "DOC",
        FileFormat.Csv => "CSV",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The extension new files of this format get, with the dot.</summary>
    public static string Extension(this FileFormat format) => "." + format.Label().ToLowerInvariant();

    public static bool IsImage(this FileFormat format) => format is
        FileFormat.Jpeg or FileFormat.Png or FileFormat.WebP or FileFormat.Heic or FileFormat.Avif
        or FileFormat.Tiff or FileFormat.Bmp or FileFormat.Gif;

    /// <summary>The Microsoft Office program that opens this format, or null for formats that aren't Office documents.</summary>
    public static OfficeApp? OpensWith(this FileFormat format) => format switch
    {
        FileFormat.Pptx or FileFormat.Ppt => OfficeApp.PowerPoint,
        FileFormat.Xlsx or FileFormat.Xls => OfficeApp.Excel,
        FileFormat.Docx or FileFormat.Doc => OfficeApp.Word,
        _ => null,
    };

    /// <summary>How the program is named in messages: "Microsoft Word".</summary>
    public static string DisplayName(this OfficeApp app) => app switch
    {
        OfficeApp.Word => "Microsoft Word",
        OfficeApp.PowerPoint => "Microsoft PowerPoint",
        OfficeApp.Excel => "Microsoft Excel",
        _ => throw new ArgumentOutOfRangeException(nameof(app), app, null),
    };

    /// <summary>A plain name for a kind of file, for messages: "PowerPoint presentations".</summary>
    public static string Plural(this FileFormat format) => format switch
    {
        FileFormat.Pdf => "PDFs",
        FileFormat.Pptx or FileFormat.Ppt => "PowerPoint presentations",
        FileFormat.Xlsx or FileFormat.Xls => "Excel workbooks",
        FileFormat.Docx or FileFormat.Doc => "Word documents",
        FileFormat.Txt => "text files",
        FileFormat.Csv => "CSV files",
        _ => format.Label() + " files",
    };

    /// <summary>Formats that keep the image bit for bit (no lossy compression).</summary>
    public static bool IsLossless(this FileFormat format) => format is
        FileFormat.Png or FileFormat.Tiff or FileFormat.Bmp or FileFormat.Gif;

    /// <summary>The format a path's extension names. No file access, so it's safe during a drag.</summary>
    public static FileFormat? FromPath(string path) =>
        ByExtension.TryGetValue(Path.GetExtension(path), out var format) ? format : null;

    /// <summary>Parses a format name typed by a user: "jpg", "JPEG", ".png", "tif".</summary>
    public static FileFormat? FromName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length == 0 ? null : FromPath(trimmed.StartsWith('.') ? trimmed : "." + trimmed);
    }

    /// <summary>
    /// Recognizes a format from the first bytes of a file (at least 32 are best; PDF headers may sit
    /// anywhere in the first 1024). Returns null when the bytes don't match any format this reads.
    /// </summary>
    public static FileFormat? FromContent(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return FileFormat.Jpeg;
        }

        if (header.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return FileFormat.Png;
        }

        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
        {
            return FileFormat.Gif;
        }

        if (header.StartsWith("II*\0"u8) || header.StartsWith("MM\0*"u8) || header.StartsWith("II+\0"u8) || header.StartsWith("MM\0+"u8))
        {
            return FileFormat.Tiff;
        }

        if (header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
        {
            return FileFormat.WebP;
        }

        if (header.Length >= 12 && header[4..8].SequenceEqual("ftyp"u8))
        {
            return IsoMediaFormat(header);
        }

        if (header.StartsWith("BM"u8) && header.Length >= 14)
        {
            return FileFormat.Bmp;
        }

        return header[..Math.Min(header.Length, 1024)].IndexOf("%PDF-"u8) >= 0 ? FileFormat.Pdf : null;
    }

    /// <summary>
    /// The format of a file on disk: its content when recognizable, otherwise its extension.
    /// Reads the file, so don't call it while a drag is in progress (cloud files would download).
    /// </summary>
    public static FileFormat? Detect(string path)
    {
        Span<byte> header = stackalloc byte[1024];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1))
        {
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        }

        return FromContent(header[..read]) ?? FromPath(path);
    }

    /// <summary>HEIF-family files (ISO base media): the brands in the ftyp box tell HEIC from AVIF.</summary>
    private static FileFormat? IsoMediaFormat(ReadOnlySpan<byte> header)
    {
        var boxSize = (int)Math.Min((uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]), (uint)header.Length);
        var brands = new List<string>();
        for (var offset = 8; offset + 4 <= boxSize; offset += 4)
        {
            if (offset != 12) // Bytes 12-15 hold the minor version, not a brand.
            {
                brands.Add(Encoding.ASCII.GetString(header.Slice(offset, 4)));
            }
        }

        if (brands.Count == 0)
        {
            return null;
        }

        if (brands[0] is "avif" or "avis" || (brands[0] is "mif1" or "msf1" && brands.Contains("avif")))
        {
            return FileFormat.Avif;
        }

        return brands.Any(brand => brand is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1")
            ? FileFormat.Heic
            : null;
    }
}
