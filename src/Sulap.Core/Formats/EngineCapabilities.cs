using ImageMagick;
using Microsoft.Win32;
using Sulap.Core.Conversion;
using Windows.Graphics.Imaging;

namespace Sulap.Core.Formats;

/// <summary>The Microsoft Office programs Sulap can use to open Office documents.</summary>
public enum OfficeApp
{
    Word,
    PowerPoint,
    Excel,
}

/// <summary>
/// Which optional engines this machine and build have. The registry hides outputs nothing can write.
/// </summary>
public sealed record EngineCapabilities
{
    /// <summary>Formats Magick.NET can write in this build (HEIC usually can't be).</summary>
    public required IReadOnlySet<FileFormat> MagickWrites { get; init; }

    /// <summary>Windows' own HEIF encoder (the HEIF/HEVC extensions from the Store).</summary>
    public required bool WindowsHeifEncoder { get; init; }

    /// <summary>Microsoft Word is installed, for PDF → DOCX with PDF Reflow and for Word documents.</summary>
    public required bool Word { get; init; }

    /// <summary>Microsoft PowerPoint is installed, for presentations.</summary>
    public required bool PowerPoint { get; init; }

    /// <summary>Microsoft Excel is installed, for workbooks.</summary>
    public required bool Excel { get; init; }

    public bool Has(OfficeApp app) => app switch
    {
        OfficeApp.Word => Word,
        OfficeApp.PowerPoint => PowerPoint,
        OfficeApp.Excel => Excel,
        _ => false,
    };

    public bool CanWrite(FileFormat format) => format switch
    {
        FileFormat.Heic => MagickWrites.Contains(FileFormat.Heic) || WindowsHeifEncoder,
        FileFormat.Pdf or FileFormat.Docx or FileFormat.Txt or FileFormat.Csv => true,
        _ => MagickWrites.Contains(format),
    };

    /// <summary>Everything available, for tests and previews.</summary>
    public static EngineCapabilities All { get; } = new()
    {
        MagickWrites = Enum.GetValues<FileFormat>().Where(format => format.IsImage()).ToHashSet(),
        WindowsHeifEncoder = true,
        Word = true,
        PowerPoint = true,
        Excel = true,
    };

    /// <summary>Checks this machine once. The HEIF and Office checks read the registry and WIC only.</summary>
    public static EngineCapabilities Detect() => new()
    {
        MagickWrites = DetectMagickWrites(),
        WindowsHeifEncoder = DetectWindowsHeifEncoder(),
        Word = DetectOffice("Word.Application"),
        PowerPoint = DetectOffice("PowerPoint.Application"),
        Excel = DetectOffice("Excel.Application"),
    };

    private static HashSet<FileFormat> DetectMagickWrites()
    {
        var writable = MagickNET.SupportedFormats
            .Where(info => info.SupportsWriting)
            .Select(info => info.Format)
            .ToHashSet();
        return Enum.GetValues<FileFormat>()
            .Where(format => format.IsImage() && ImageCodec.ToMagickFormat(format) is { } magick && writable.Contains(magick))
            .ToHashSet();
    }

    private static bool DetectWindowsHeifEncoder()
    {
        try
        {
            return BitmapEncoder.GetEncoderInformationEnumerator().Any(info => info.CodecId == BitmapEncoder.HeifEncoderId);
        }
        catch (Exception ex) when (ex is TypeLoadException or System.Runtime.InteropServices.COMException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether an Office program is registered for automation, which is what installing it does.</summary>
    private static bool DetectOffice(string progId)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"{progId}\CLSID");
            return key?.GetValue(null) is string { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
