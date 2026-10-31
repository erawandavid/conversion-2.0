using System.Text;
using ImageMagick;
using ImageMagick.Drawing;

namespace Sulap.Core.Tests;

/// <summary>Test files made in code, so no binary fixtures live in the repository.</summary>
internal static class Fixtures
{
    /// <summary>The marker drawn in the stored (unrotated) top-left corner of <see cref="PhotoJpeg"/>.</summary>
    public static readonly MagickColor Marker = new(255, 0, 0);

    /// <summary>
    /// A US Letter PDF with real text: one page per entry, lines separated by '\n'. Written byte by byte
    /// with the built-in Helvetica font, so it needs no font files and no PDF library.
    /// </summary>
    public static string TextPdf(string path, params string[] pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select((_, i) => $"{4 + (i * 2)} 0 R"))}] /Count {pages.Length} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        for (var i = 0; i < pages.Length; i++)
        {
            var lines = pages[i].Split('\n').Select(line => $"({line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)")}) Tj");
            var content = $"BT /F1 18 Tf 72 700 Td 24 TL {string.Join(" T* ", lines)} ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (i * 2)} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append($"{offset:D10} 00000 n \n");
        }

        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);
        return path;
    }

    /// <summary>
    /// A 120 x 80 JPEG stored sideways: EXIF orientation 6 says "rotate 90° clockwise to view", so it
    /// shows as 80 x 120 with the red marker at the top right. Has camera EXIF and a GPS location.
    /// </summary>
    public static string PhotoJpeg(string path)
    {
        using var image = new MagickImage(new MagickColor(40, 90, 200), 120, 80);
        new Drawables().FillColor(Marker).Rectangle(0, 0, 29, 19).Draw(image);

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Sulap Test");
        exif.SetValue(ExifTag.Model, "Fixture Camera");
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.GPSLatitudeRef, "S");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(6, 1), new Rational(10, 1), new Rational(30, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(106, 1), new Rational(49, 1), new Rational(0, 1)]);
        image.SetProfile(exif);
        image.Orientation = Orientation.RightTop;
        image.Quality = 95;
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    /// <summary>A 64 x 64 PNG: the left half is opaque green, the right half fully transparent.</summary>
    public static string AlphaPng(string path, uint width = 64, uint height = 64)
    {
        using var image = new MagickImage(MagickColors.Transparent, width, height);
        new Drawables().FillColor(new MagickColor(0, 160, 0)).Rectangle(0, 0, (width / 2) - 1, height - 1).Draw(image);
        image.Write(path, MagickFormat.Png32);
        return path;
    }

    /// <summary>An opaque PNG of a given size, for checking page order and page sizes.</summary>
    public static string SolidPng(string path, uint width, uint height)
    {
        using var image = new MagickImage(new MagickColor(200, 200, 40), width, height);
        image.Density = new Density(72, 72, DensityUnit.PixelsPerInch);
        image.Write(path, MagickFormat.Png24);
        return path;
    }

    /// <summary>A picture of black text on white, like a scanned page, for OCR.</summary>
    public static string TextImage(string path, string text)
    {
        using var image = new MagickImage(MagickColors.White, 1700, 400);
        new Drawables()
            .Font(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf"))
            .FontPointSize(110)
            .FillColor(MagickColors.Black)
            .Text(80, 250, text)
            .Draw(image);
        image.Density = new Density(200, 200, DensityUnit.PixelsPerInch);
        image.Write(path, MagickFormat.Png24);
        return path;
    }

    /// <summary>A PDF that can only be opened with a password.</summary>
    public static string PasswordPdf(string path)
    {
        using var document = new PdfSharp.Pdf.PdfDocument();
        document.AddPage();
        document.SecuritySettings.UserPassword = "secret";
        document.SecuritySettings.OwnerPassword = "owner";
        document.Save(path);
        return path;
    }
}

/// <summary>A fresh folder for one test, deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sulap-tests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Everything in the folder, by name, including hidden items.</summary>
    public string[] Names() => [.. Directory.EnumerateFileSystemEntries(Path).Select(entry => System.IO.Path.GetFileName(entry)).Order(StringComparer.Ordinal)];

    public void Dispose()
    {
        foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            entry.Attributes = entry is DirectoryInfo ? FileAttributes.Directory : FileAttributes.Normal;
        }

        Directory.Delete(Path, recursive: true);
    }
}
