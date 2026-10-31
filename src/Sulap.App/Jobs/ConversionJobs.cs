using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Sulap.App.Platform;
using Sulap.App.Views;
using Sulap.Core;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;
using Sulap.Core.Formatting;

namespace Sulap.App.Jobs;

/// <summary>
/// Runs conversions off the UI thread, each with its own toast: progress while it runs, then what was
/// saved, what went wrong, or a way to pick another folder. The dragged files are only ever read.
/// </summary>
internal sealed class ConversionJobs(ConversionEngine engine)
{
    private static readonly TimeSpan ShortClose = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan NormalClose = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan LongClose = TimeSpan.FromSeconds(20);

    private readonly List<(Task Work, CancellationTokenSource Cancel)> _running = []; // UI thread only.

    /// <summary>Raised on the UI thread when a conversion is over; the result is null when it was cancelled or broke.</summary>
    public event Action<ConversionRequest, ConversionResult?>? Finished;

    public void Start(ConversionRequest request) => _ = RunAsync(request, toast: null);

    /// <summary>
    /// Cancels every conversion and waits a little, so they can remove their temporary files and close
    /// the hidden Word they started. Waits on the background work only, never on the UI thread.
    /// </summary>
    public void CancelAll(TimeSpan wait)
    {
        foreach (var (_, cancel) in _running)
        {
            cancel.Cancel();
        }

        try
        {
            Task.WaitAll([.. _running.Select(job => job.Work)], wait);
        }
        catch (AggregateException)
        {
            // Cancelled or failed: either way it's over.
        }
    }

    private static string ProgressTitle(ConversionRequest request)
    {
        var target = request.Target.Label();
        if (request.Sources.Count == 1)
        {
            return $"Converting {Path.GetFileName(request.Sources[0])} to {target}";
        }

        return request.Target == FileFormat.Pdf && request.Sources.All(source => FileFormats.FromPath(source) is { } format && format.IsImage())
            ? $"Combining {request.Sources.Count} images into a PDF"
            : $"Converting {request.Sources.Count} files to {target}";
    }

    private static string Summary(IReadOnlyList<ConversionOutput> outputs)
    {
        if (outputs.Count != 1)
        {
            return $"{FileSize.Format(outputs.Sum(output => output.Bytes))} in total";
        }

        var output = outputs[0];
        var size = FileSize.Format(output.Bytes);
        return output.IsFolder ? $"{output.ItemsText} in a folder, {size}"
            : output.Items > 1 ? $"{output.ItemsText}, {size}"
            : size;
    }

    /// <summary>The first problems, then how many more; then the notes. One per line.</summary>
    private static string Lines(string? first, IReadOnlyList<ConversionFailure> failures, IReadOnlyList<string> notes)
    {
        const int shown = 2;
        var lines = new List<string>();
        if (first is not null)
        {
            lines.Add(first);
        }

        lines.AddRange(failures.Take(shown).Select(failure => failure.Message));
        if (failures.Count > shown)
        {
            lines.Add($"{failures.Count - shown} more couldn't be converted.");
        }

        lines.AddRange(notes);
        return string.Join(Environment.NewLine, lines);
    }

    private static string FolderName(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return "that folder";
        }

        var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            Shell.ShowInExplorer(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException)
        {
            AppLog.Write($"Couldn't show {path} in File Explorer: {ex.Message}");
        }
    }

    private static void Log(ConversionResult result)
    {
        foreach (var failure in result.Failures.Where(failure => failure.Detail is not null))
        {
            AppLog.Write($"{failure.Source}: {failure.Message}{Environment.NewLine}{failure.Detail}");
        }
    }

    private async Task RunAsync(ConversionRequest request, ToastWindow? toast)
    {
        ConversionResult? result = null;
        try
        {
            toast ??= ToastWindow.Open();
            using var cancel = new CancellationTokenSource();
            void OnCancel() => cancel.Cancel();
            toast.CancelRequested += OnCancel;
            toast.ShowProgress(ProgressTitle(request), "Starting…");
            var shownToast = toast;
            var progress = new Progress<ConversionProgress>(step => shownToast.UpdateProgress(step.Status, step.Fraction));
            (Task Work, CancellationTokenSource Cancel)? job = null;
            try
            {
                var converter = await engine.Converter;
                var work = Task.Run(() => converter.ConvertAsync(request, progress, cancel.Token), cancel.Token);
                job = (work, cancel);
                _running.Add(job.Value);
                result = await work;
                Log(result);
                toast.ShowResult(Describe(request, result, toast));
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                var kept = request.Sources.Count == 1 ? "Nothing was saved." : "Files that were already done were kept.";
                toast.ShowResult(new ToastContent(ToastKind.Info, "Conversion cancelled", kept, CloseAfter: ShortClose));
            }
            finally
            {
                toast.CancelRequested -= OnCancel;
                if (job is { } running)
                {
                    _running.Remove(running);
                }
            }
        }
        catch (Exception ex)
        {
            result = null;
            AppLog.Write($"Converting {string.Join(", ", request.Sources)} to {request.Target.Label()} failed: {ex}");
            toast?.ShowResult(new ToastContent(
                ToastKind.Error,
                $"Couldn't convert to {request.Target.Label()}",
                "Something went wrong. The details are in the log.",
                [new ToastAction("Show log", () => ShowInExplorer(AppLog.FilePath), Dismisses: true)],
                LongClose));
        }

        try
        {
            Finished?.Invoke(request, result);
        }
        catch (Exception ex)
        {
            AppLog.Write($"A conversion listener failed: {ex}");
        }
    }

    private ToastContent Describe(ConversionRequest request, ConversionResult result, ToastWindow toast)
    {
        var outputs = result.Outputs;
        var actions = new List<ToastAction>();
        if (outputs.Count > 0)
        {
            actions.Add(new ToastAction("Show in folder", () => ShowInExplorer(outputs[0].Path), Dismisses: true));
        }

        if (result.NeedFolder.Count > 0)
        {
            var copies = result.NeedFolder.Count == 1 ? "copy" : "copies";
            actions.Insert(0, new ToastAction("Choose folder…", () => ChooseFolder(request, result, toast), IsPrimary: true));
            var first = $"{AppIdentity.Name} can't save files there. Choose another folder for the converted {copies}.";
            if (outputs.Count > 0)
            {
                first += $" Saved so far: {(outputs.Count == 1 ? Path.GetFileName(outputs[0].Path) : $"{outputs.Count} files")}.";
            }

            return new ToastContent(ToastKind.Warning, $"Can't save in {FolderName(result.UnwritableFolder)}", Lines(first, result.Failures, result.Notes), actions);
        }

        if (outputs.Count == 0)
        {
            return new ToastContent(ToastKind.Error, $"Couldn't convert to {request.Target.Label()}", Lines(null, result.Failures, result.Notes), actions, LongClose);
        }

        var title = outputs.Count == 1 ? $"Saved {Path.GetFileName(outputs[0].Path)}" : $"Saved {outputs.Count} files";
        return result.Failures.Count == 0
            ? new ToastContent(ToastKind.Success, title, Lines(Summary(outputs), [], result.Notes), actions, result.Notes.Count > 0 ? LongClose : NormalClose)
            : new ToastContent(ToastKind.Warning, title, Lines(Summary(outputs), result.Failures, result.Notes), actions, LongClose);
    }

    /// <summary>The source folder can't be written to: ask for another one and convert what's left there.</summary>
    private void ChooseFolder(ConversionRequest request, ConversionResult result, ToastWindow toast)
    {
        var dialog = new OpenFolderDialog
        {
            Title = result.NeedFolder.Count == 1 ? "Choose where to save the converted copy" : "Choose where to save the converted copies",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(toast) == true)
        {
            _ = RunAsync(request with { Sources = result.NeedFolder, OutputFolder = dialog.FolderName }, toast);
        }
    }
}
