using Sulap.Core.Formats;

namespace Sulap.Core.Conversion;

/// <summary>Convert <see cref="Sources"/> to <see cref="Target"/>.</summary>
public sealed record ConversionRequest(IReadOnlyList<string> Sources, FileFormat Target)
{
    /// <summary>Where to save. Null saves next to each source file.</summary>
    public string? OutputFolder { get; init; }

    public ConversionOptions Options { get; init; } = new();
}

public sealed record ConversionOptions
{
    /// <summary>Resolution for PDF pages and presentation slides turned into images.</summary>
    public int PdfDpi { get; init; } = 300;

    /// <summary>Quality (1–100) for lossy formats: JPG, WEBP, AVIF, HEIC.</summary>
    public int Quality { get; init; } = 90;

    /// <summary>Use Microsoft Word for PDF → DOCX when it's installed.</summary>
    public bool UseWord { get; init; } = true;

    /// <summary>
    /// How long an Office program may go without finishing a step (a slide, a sheet, or the whole file
    /// when it can't report progress) before it's given up on and closed.
    /// </summary>
    public TimeSpan OfficeTimeout { get; init; } = TimeSpan.FromMinutes(3);
}

/// <param name="Fraction">0 to 1, or null while the length of the work is unknown.</param>
public readonly record struct ConversionProgress(double? Fraction, string Status);

/// <param name="Items">Pages, slides, or sheets in the result, or files in a folder; 1 for a single image.</param>
/// <param name="Unit">What <paramref name="Items"/> counts: "page", "slide", "sheet", or "file".</param>
public sealed record ConversionOutput(string Path, bool IsFolder, long Bytes, int Items, string Unit = "page")
{
    /// <summary>"3 pages", "1 slide".</summary>
    public string ItemsText => Items == 1 ? $"1 {Unit}" : $"{Items} {Unit}s";
}

/// <param name="Message">Plain language, ready to show to the user.</param>
/// <param name="Detail">Technical details for the log, when there are any.</param>
public sealed record ConversionFailure(string Source, string Message, string? Detail = null);

public sealed record ConversionResult(FileFormat Target)
{
    public IReadOnlyList<ConversionOutput> Outputs { get; init; } = [];

    public IReadOnlyList<ConversionFailure> Failures { get; init; } = [];

    /// <summary>Things worth telling the user, such as "made without Word".</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Sources that weren't converted because their folder can't be written to. Retry them with an output folder.</summary>
    public IReadOnlyList<string> NeedFolder { get; init; } = [];

    /// <summary>The folder that couldn't be written to, when <see cref="NeedFolder"/> isn't empty.</summary>
    public string? UnwritableFolder { get; init; }

    public bool Succeeded => Failures.Count == 0 && NeedFolder.Count == 0;
}

/// <summary>A failure whose message can be shown to the user as it is.</summary>
public sealed class ConversionException(string message, Exception? inner = null) : Exception(message, inner);
