#if DEBUG
namespace Sulap.App.Diagnostics;

/// <summary>
/// Command-line switches that only exist in Debug builds:
/// <c>--render-ui &lt;folder&gt;</c> renders the tray icon and the windows to PNG files, so the UI can be
/// checked where nobody is looking at a screen (CI, cloud sessions).
/// </summary>
internal static class DebugCommands
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        if (args is ["--render-ui", var folder])
        {
            exitCode = UiSnapshots.RenderAll(folder);
            return true;
        }

        exitCode = 0;
        return false;
    }
}
#endif
