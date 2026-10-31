using ImageMagick;
using Sulap.Core.Formats;
using Sulap.Core.Formatting;
using Sulap.Core.Output;

namespace Sulap.Core.Conversion;

/// <summary>
/// Runs a conversion: checks each source, plans the jobs (several images → PDF become one combined
/// PDF; everything else converts file by file), runs them, and reports what happened in plain words.
/// One failing file never stops the others.
/// </summary>
public sealed class Converter(FormatRegistry registry)
{
    private const int ErrorDiskFull = unchecked((int)0x80070070);
    private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    public FormatRegistry Registry { get; } = registry;

    /// <summary>
    /// Puts right what a conversion that was closed halfway left in Office's settings: an option it
    /// switched on, and Office's notes about the copy it was working on. Only those are touched.
    /// Reads one small file and a few registry keys; call it once at startup, off the UI thread.
    /// </summary>
    public static void RestoreOfficeSettings() => OfficeAutomation.RestoreLeftovers();

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request, IProgress<ConversionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var failures = new List<ConversionFailure>();
        var jobs = Plan(request, failures);
        var outputs = new List<ConversionOutput>();
        var notes = new List<string>();
        var needFolder = new List<string>();
        string? unwritableFolder = null;

        for (var index = 0; index < jobs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[index];
            var jobIndex = index;
            void Report(ConversionProgress step)
            {
                var fraction = jobs.Count == 1 ? step.Fraction : (jobIndex + (step.Fraction ?? 0)) / jobs.Count;
                var status = jobs.Count == 1 ? step.Status : $"File {jobIndex + 1} of {jobs.Count}";
                progress?.Report(new ConversionProgress(fraction, status));
            }

            Report(new ConversionProgress(0, "Starting"));
            var folder = request.OutputFolder ?? Path.GetDirectoryName(Path.GetFullPath(job.Sources[0].Path))!;
            if (unwritableFolder is not null && string.Equals(folder, unwritableFolder, StringComparison.OrdinalIgnoreCase))
            {
                needFolder.AddRange(job.Sources.Select(source => source.Path));
                continue;
            }

            try
            {
                OutputWriter.EnsureWritable(folder);
                var (output, jobNotes) = await RunAsync(job, request.Target, folder, request.Options, Report, cancellationToken);
                outputs.Add(output);
                notes.AddRange(jobNotes.Where(note => !notes.Contains(note)));
            }
            catch (OutputFolderNotWritableException ex) when (request.OutputFolder is null)
            {
                unwritableFolder = ex.Folder;
                needFolder.AddRange(job.Sources.Select(source => source.Path));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(Describe(job, request.Target, ex));
            }
        }

        progress?.Report(new ConversionProgress(1, "Done"));
        return new ConversionResult(request.Target)
        {
            Outputs = outputs,
            Failures = failures,
            Notes = notes,
            NeedFolder = needFolder,
            UnwritableFolder = unwritableFolder,
        };
    }

    private List<Job> Plan(ConversionRequest request, List<ConversionFailure> failures)
    {
        var target = request.Target;
        var sources = new List<(string Path, FileFormat Format)>();
        foreach (var source in request.Sources.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(source.TrimEnd('\\', '/'));
            if (Directory.Exists(source))
            {
                failures.Add(new ConversionFailure(source, $"{name} is a folder. Select the files inside it instead."));
                continue;
            }

            if (!File.Exists(source))
            {
                failures.Add(new ConversionFailure(source, $"{name} can't be found. It may have been moved or deleted."));
                continue;
            }

            FileFormat? format;
            try
            {
                format = FileFormats.Detect(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new ConversionFailure(source, SourceProblem(name, ex) ?? $"{name} couldn't be opened.", ex.ToString()));
                continue;
            }

            if (format is not { } known)
            {
                failures.Add(new ConversionFailure(source, $"{AppIdentity.Name} can't convert {name}: it isn't a type of file it knows."));
            }
            else if (known == target)
            {
                failures.Add(new ConversionFailure(source, $"{name} is already a {target.Label()} file."));
            }
            else if (!Registry.CanConvert(known, target))
            {
                failures.Add(new ConversionFailure(source, Registry.WhyNot(known, target) is { } reason ? $"{name}: {reason}" : $"{name} can't be converted to {target.Label()}."));
            }
            else
            {
                sources.Add((source, known));
            }
        }

        if (target == FileFormat.Pdf && sources.Count > 1 && sources.All(source => source.Format.IsImage()))
        {
            // One combined PDF, pages in File Explorer's name order, named after the first image.
            return [new Job([.. sources.OrderBy(source => Path.GetFileName(source.Path), NaturalOrder.FileNames)])];
        }

        return sources.Select(source => new Job([source])).ToList();
    }

    private async Task<(ConversionOutput Output, IReadOnlyList<string> Notes)> RunAsync(
        Job job, FileFormat target, string folder, ConversionOptions options, Action<ConversionProgress> report,
        CancellationToken cancellationToken)
    {
        var (source, format) = job.Sources[0];
        var capabilities = Registry.Capabilities;
        if (format.IsImage())
        {
            return target switch
            {
                FileFormat.Pdf => (await ImageDocuments.ToPdfAsync(job.Sources, folder, OutputNames.BaseName(source), report, cancellationToken), []),
                FileFormat.Docx => (await ImageDocuments.ToDocxAsync(source, format, folder, cancellationToken), []),
                _ when target.IsImage() => (await ImageCodec.ConvertAsync(source, format, target, folder, options, capabilities, cancellationToken), HeicNotes(target)),
                _ => throw Unsupported(source, target),
            };
        }

        if (format == FileFormat.Pdf)
        {
            return target switch
            {
                FileFormat.Jpeg or FileFormat.Png => (await PdfConversions.ToImagesAsync(source, target, folder, options, report, cancellationToken), []),
                FileFormat.Txt => await PdfConversions.ToTextAsync(source, folder, report, cancellationToken),
                FileFormat.Docx => await PdfConversions.ToDocxAsync(source, folder, options, capabilities, report, cancellationToken),
                _ => throw Unsupported(source, target),
            };
        }

        if (format.OpensWith() is not null)
        {
            return await OfficeConversions.ConvertAsync(source, format, target, folder, options, report, cancellationToken);
        }

        throw Unsupported(source, target);
    }

    private IReadOnlyList<string> HeicNotes(FileFormat target) =>
        target == FileFormat.Heic && !Registry.Capabilities.MagickWrites.Contains(FileFormat.Heic)
            ? ["Saved with Windows' HEIF encoder, which doesn't keep the photo's details (camera, date, location)."]
            : [];

    private static ConversionException Unsupported(string source, FileFormat target) =>
        new($"{Path.GetFileName(source)} can't be converted to {target.Label()}.");

    private static ConversionFailure Describe(Job job, FileFormat target, Exception ex)
    {
        var source = job.Sources[0].Path;
        var name = job.Sources.Count == 1 ? Path.GetFileName(source) : $"{job.Sources.Count} images";
        var message = ex switch
        {
            ConversionException => ex.Message,
            OutputFolderNotWritableException notWritable => $"Can't save files in {notWritable.Folder}.",
            IOException { HResult: ErrorDiskFull or ErrorHandleDiskFull } => "There isn't enough space on the disk to save the result.",
            MagickResourceLimitErrorException or OutOfMemoryException or InsufficientMemoryException => $"{name} is too large to convert.",
            MagickException => $"{name} couldn't be read. The file may be damaged.",
            PathTooLongException => $"The path of {name} is too long. Move it to a shorter folder path and try again.",
            _ => SourceProblem(name, ex) ?? $"Something went wrong while converting {name} to {target.Label()}.",
        };
        return new ConversionFailure(source, message, ex is ConversionException { InnerException: null } ? null : ex.ToString());
    }

    private static string? SourceProblem(string name, Exception ex) => ex switch
    {
        FileNotFoundException or DirectoryNotFoundException => $"{name} can't be found. It may have been moved or deleted.",
        IOException { HResult: ErrorSharingViolation or ErrorLockViolation } => $"{name} is open in another app. Close it there and try again.",
        UnauthorizedAccessException => $"{name} can't be opened: access is denied.",
        _ => null,
    };

    private sealed record Job(IReadOnlyList<(string Path, FileFormat Format)> Sources);
}
