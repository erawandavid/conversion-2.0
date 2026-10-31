using System.Diagnostics;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;

namespace Sulap.App.Jobs;

/// <summary>
/// The conversion engine, prepared in the background at startup: finding out what this machine can
/// write loads Magick.NET's native code, which takes a moment the UI thread shouldn't spend.
/// </summary>
internal sealed class ConversionEngine
{
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(3);

    private readonly Task<Converter> _converter = Task.Run(Create);

    public Task<Converter> Converter => _converter;

    /// <summary>
    /// What the wheels offer. Waits briefly when the engine isn't ready yet, which only happens right
    /// after startup; null when it still isn't.
    /// </summary>
    public FormatRegistry? Registry
    {
        get
        {
            try
            {
                _converter.Wait(ReadyWait);
            }
            catch (AggregateException)
            {
                // Create doesn't throw; this only keeps a surprise from reaching a drag event.
            }

            return _converter.IsCompletedSuccessfully ? _converter.Result.Registry : null;
        }
    }

    private static Converter Create()
    {
        var started = Stopwatch.GetTimestamp();
        Sulap.Core.Conversion.Converter.RestoreOfficeSettings(); // Qualified: the Converter property hides the type here.
        EngineCapabilities capabilities;
        try
        {
            capabilities = EngineCapabilities.Detect();
            var writes = string.Join(", ", capabilities.MagickWrites.Order().Select(format => format.Label()));
            AppLog.Write($"Engines ready in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms. Magick.NET writes {writes}; "
                + $"Windows HEIF encoder: {(capabilities.WindowsHeifEncoder ? "yes" : "no")}; "
                + $"Microsoft Word: {(capabilities.Word ? "yes" : "no")}, PowerPoint: {(capabilities.PowerPoint ? "yes" : "no")}, Excel: {(capabilities.Excel ? "yes" : "no")}.");
        }
        catch (Exception ex)
        {
            // Without Magick.NET no image can be read, but PDFs still convert to text and DOCX.
            AppLog.Write($"Checking the conversion engines failed: {ex}");
            capabilities = new EngineCapabilities { MagickWrites = new HashSet<FileFormat>(), WindowsHeifEncoder = false, Word = false, PowerPoint = false, Excel = false };
        }

        return new Converter(new FormatRegistry(capabilities));
    }
}
