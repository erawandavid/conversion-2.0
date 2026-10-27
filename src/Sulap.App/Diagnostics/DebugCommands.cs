#if DEBUG
namespace Sulap.App.Diagnostics;

/// <summary>
/// Command-line switches that only exist in Debug builds, for checks where nobody is looking at a
/// screen (CI):
/// <list type="bullet">
/// <item><c>--render-ui &lt;folder&gt;</c> renders the tray icon and the windows to PNG files.</item>
/// <item><c>--spike-smoke &lt;folder&gt;</c> runs the drag detection log for a few seconds and saves its log.</item>
/// </list>
/// </summary>
internal static class DebugCommands
{
    public static bool IsDebugCommand(string[] args) => args is ["--render-ui", _] or ["--spike-smoke", _];

    public static int Run(string[] args) => args switch
    {
        ["--render-ui", var folder] => UiSnapshots.RenderAll(folder),
        ["--spike-smoke", var folder] => SpikeSmokeTest.Run(folder),
        _ => 2,
    };
}
#endif
