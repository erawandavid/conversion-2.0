#if DEBUG
using System.IO;
using System.Windows.Threading;
using Sulap.App.Spike;
using Sulap.Core.Settings;

namespace Sulap.App.Diagnostics;

/// <summary>Opens the drag detection log, lets its hooks run for a few seconds, and keeps the log.</summary>
internal static class SpikeSmokeTest
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(3);

    public static int Run(string folder)
    {
        Directory.CreateDirectory(folder);
        try
        {
            var window = new DragSpikeWindow(new TriggerKeys());
            window.Show();

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(Duration, DispatcherPriority.Normal, (_, _) => frame.Continue = false, window.Dispatcher);
            Dispatcher.PushFrame(frame);
            timer.Stop();

            var logPath = window.LogFilePath;
            window.Close();
            File.Copy(logPath, Path.Combine(folder, "drag-spike.log"), overwrite: true);
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "spike-error.txt"), ex.ToString());
            return 1;
        }
    }
}
#endif
