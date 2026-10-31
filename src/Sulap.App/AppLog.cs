using System.IO;
using Sulap.Core;

namespace Sulap.App;

/// <summary>
/// A small log at <c>%AppData%\{AppName}\logs\app.log</c> for what the app couldn't do and why:
/// drag signals that didn't start, the technical details behind a failed conversion. Crashes go to
/// <see cref="CrashLog"/> instead. Kept under about 1 MB by starting over in a new file.
/// </summary>
internal static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppIdentity.LogDirectory, "app.log");

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(AppIdentity.LogDirectory);
                if (new FileInfo(FilePath) is { Exists: true, Length: > MaxBytes })
                {
                    File.Move(FilePath, Path.Combine(AppIdentity.LogDirectory, "app.old.log"), overwrite: true);
                }

                File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never be the reason something fails.
            }
        }
    }
}
