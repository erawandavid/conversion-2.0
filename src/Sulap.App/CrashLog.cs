using System.IO;
using System.Windows;
using Sulap.Core;

namespace Sulap.App;

/// <summary>Writes unhandled exceptions to <c>%AppData%\{AppName}\logs\crash.log</c>.</summary>
internal static class CrashLog
{
    public static string FilePath => Path.Combine(AppIdentity.LogDirectory, "crash.log");

    public static void Install(Application app)
    {
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write(e.Exception);
            e.Handled = true;
            MessageBox.Show(
                $"{AppIdentity.Name} ran into a problem and has to close.\n\nDetails were saved to:\n{FilePath}",
                AppIdentity.Name,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            app.Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            e.SetObserved();
        };
    }

    public static void Write(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(AppIdentity.LogDirectory);
            File.AppendAllText(
                FilePath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {AppIdentity.Name} {AppIdentity.Version}{Environment.NewLine}"
                + $"{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere left to report it; never throw from the crash handler.
        }
    }
}
