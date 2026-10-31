using Sulap.Core.Formats;

namespace Sulap.Core.Output;

/// <summary>Names for converted files: the original's base name, never an existing file's name.</summary>
public static class OutputNames
{
    /// <summary>"Report" + ".jpg": "Report.jpg" for attempt 1, then "Report (2).jpg", "Report (3).jpg"…</summary>
    public static string Candidate(string baseName, string extension, int attempt) =>
        attempt <= 1 ? baseName + extension : $"{baseName} ({attempt}){extension}";

    /// <summary>The first candidate in <paramref name="folder"/> that no file or folder uses yet.</summary>
    public static string FirstFree(string folder, string baseName, string extension)
    {
        for (var attempt = 1; ; attempt++)
        {
            var path = Path.Combine(folder, Candidate(baseName, extension, attempt));
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return path;
            }
        }
    }

    /// <summary>The folder that holds the pages of a multi-page PDF: "Report (JPG)".</summary>
    public static string PagesFolder(string baseName, FileFormat format) => $"{baseName} ({format.Label()})";

    /// <summary>One page inside that folder: "Page 1.jpg".</summary>
    public static string PageFile(int pageNumber, FileFormat format) => $"Page {pageNumber}{format.Extension()}";

    /// <summary>One slide of a presentation inside its folder: "Slide 3.png". The number is the slide's own, so hidden slides leave gaps.</summary>
    public static string SlideFile(int slideNumber, FileFormat format) => $"Slide {slideNumber}{format.Extension()}";

    /// <summary>
    /// A sheet's name as a file name: "Q1/Q2" can't be one, nor can "CON". Characters Windows doesn't allow
    /// in names are replaced, and a name that's still unusable becomes "Sheet".
    /// </summary>
    public static string SheetFile(string sheetName, FileFormat format)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. sheetName.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)]).Trim().TrimEnd('.');
        if (cleaned.Length > 100)
        {
            cleaned = cleaned[..100].TrimEnd();
        }

        var firstPart = cleaned.Split('.')[0].TrimEnd();
        if (cleaned.Length == 0 || IsReservedDeviceName(firstPart))
        {
            cleaned = "Sheet";
        }

        return cleaned + format.Extension();
    }

    private static bool IsReservedDeviceName(string name) =>
        name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
        || (name.Length == 4 && name[3] is >= '1' and <= '9' && name[..3].ToUpperInvariant() is "COM" or "LPT");

    /// <summary>A file's name without its extension.</summary>
    public static string BaseName(string path) => Path.GetFileNameWithoutExtension(path);
}
