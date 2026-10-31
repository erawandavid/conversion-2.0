namespace Sulap.Core.Formats;

/// <summary>One wedge of the Convert wheel.</summary>
/// <param name="Hint">Why the option can't be used, when <see cref="IsEnabled"/> is false.</param>
public sealed record ConvertOption(FileFormat Target, bool IsEnabled = true, string? Hint = null)
{
    public string Label => Target.Label();
}

/// <summary>
/// Maps each input format to what it can become, in wheel order (clockwise from the top). The wheels
/// are built from this, so the UI has nothing format-specific in it.
/// </summary>
public sealed class FormatRegistry(EngineCapabilities capabilities)
{
    /// <summary>The most wedges a wheel shows.</summary>
    public const int MaxWedges = 8;

    // Every image target, in wheel order. GIF comes last: with eight wedges it's the one left off the
    // wheel, but the engine (and the CLI) still convert to it.
    private static readonly FileFormat[] ImageTargets =
    [
        FileFormat.Jpeg, FileFormat.Png, FileFormat.WebP, FileFormat.Heic, FileFormat.Tiff,
        FileFormat.Avif, FileFormat.Bmp, FileFormat.Pdf, FileFormat.Docx, FileFormat.Gif,
    ];

    // DOCX top, JPG right, PNG bottom, TXT left: the reference layout for a PDF.
    private static readonly FileFormat[] PdfTargets = [FileFormat.Docx, FileFormat.Jpeg, FileFormat.Png, FileFormat.Txt];

    // The order for files of different kinds, so a wheel doesn't depend on which file was picked first.
    private static readonly FileFormat[] MixedOrder =
    [
        FileFormat.Docx, FileFormat.Pdf, FileFormat.Jpeg, FileFormat.Png, FileFormat.Txt, FileFormat.Csv,
        FileFormat.WebP, FileFormat.Heic, FileFormat.Tiff, FileFormat.Avif, FileFormat.Bmp, FileFormat.Gif,
    ];

    public EngineCapabilities Capabilities { get; } = capabilities;

    /// <summary>Everything the engine turns this format into, in wheel order. Never the format itself.</summary>
    public IReadOnlyList<FileFormat> Targets(FileFormat input) => [.. Routes(input).Select(route => route.Target)];

    /// <summary>Whether the engine can convert this input to this target on this machine.</summary>
    public bool CanConvert(FileFormat input, FileFormat target) =>
        Routes(input).FirstOrDefault(route => route.Target == target) is { } found
        && Capabilities.CanWrite(target)
        && (found.Needs is not { } app || Capabilities.Has(app));

    /// <summary>
    /// Why a conversion the engine knows can't run here, in words for the user; null when it can run
    /// or when this isn't a conversion the engine knows at all.
    /// </summary>
    public string? WhyNot(FileFormat input, FileFormat target) =>
        Routes(input).FirstOrDefault(route => route.Target == target) is { Needs: { } app } && !Capabilities.Has(app)
            ? $"Converting {input.Plural()} to {target.Label()} needs {app.DisplayName()}, which isn't installed."
            : null;

    /// <summary>The Convert wheel for one input format.</summary>
    public IReadOnlyList<ConvertOption> ConvertOptions(FileFormat input) => [.. Options(input).Take(MaxWedges)];

    /// <summary>
    /// The Convert wheel for several dragged files: only options valid for every one of them, in
    /// wheel order. Several images → PDF makes one combined PDF.
    /// </summary>
    public IReadOnlyList<ConvertOption> ConvertOptions(IReadOnlyCollection<FileFormat> inputs)
    {
        var distinct = inputs.Distinct().ToList();
        if (distinct.Count <= 1)
        {
            return distinct.Count == 0 ? [] : ConvertOptions(distinct[0]);
        }

        var perInput = distinct.Select(input => Options(input).ToDictionary(option => option.Target)).ToList();
        var order = distinct.All(format => format.IsImage()) ? ImageTargets : MixedOrder;
        return
        [
            .. order
                .Where(target => perInput.All(options => options.ContainsKey(target)))
                // One file that can't be converted here (no Word) turns the shared wedge off for all.
                .Select(target => perInput.Select(options => options[target]).FirstOrDefault(option => !option.IsEnabled) ?? perInput[0][target])
                .Take(MaxWedges),
        ];
    }

    /// <summary>What a kind of file can become, and the Office program that does it when one is needed.</summary>
    private sealed record Route(FileFormat Target, OfficeApp? Needs = null);

    private static List<Route> Routes(FileFormat input) => input switch
    {
        FileFormat.Pdf => [.. PdfTargets.Select(target => new Route(target))],
        _ when input.IsImage() => [.. ImageTargets.Where(target => target != input).Select(target => new Route(target))],
        FileFormat.Pptx or FileFormat.Ppt =>
            [new(FileFormat.Pdf, OfficeApp.PowerPoint), new(FileFormat.Jpeg, OfficeApp.PowerPoint), new(FileFormat.Png, OfficeApp.PowerPoint)],
        FileFormat.Xlsx or FileFormat.Xls =>
            [new(FileFormat.Pdf, OfficeApp.Excel), new(FileFormat.Csv, OfficeApp.Excel)],
        FileFormat.Docx or FileFormat.Doc => [new(FileFormat.Pdf, OfficeApp.Word), new(FileFormat.Txt, OfficeApp.Word)],
        _ => [],
    };

    private IEnumerable<ConvertOption> Options(FileFormat input) =>
        Routes(input).Select(Option).OfType<ConvertOption>();

    private ConvertOption? Option(Route route)
    {
        if (!Capabilities.CanWrite(route.Target))
        {
            // HEIC is worth showing disabled: Windows can write it once the user adds the extensions.
            // The hint is shown inside the wheel's hub, so it has to stay short.
            return route.Target == FileFormat.Heic
                ? new ConvertOption(route.Target, IsEnabled: false, Hint: "Needs the HEIF and HEVC extensions from the Microsoft Store.")
                : null;
        }

        return route.Needs is { } app && !Capabilities.Has(app)
            ? new ConvertOption(route.Target, IsEnabled: false, Hint: $"Needs {app.DisplayName()}.")
            : new ConvertOption(route.Target);
    }
}
