#if DEBUG
namespace Sulap.App.Diagnostics;

/// <summary>
/// Command-line switches that only exist in Debug builds, for checks where nobody is looking at a
/// screen (CI, cloud sessions):
/// <list type="bullet">
/// <item><c>--render-ui &lt;folder&gt;</c> renders the tray icon, the windows, the wheel, and the toasts to PNG files.</item>
/// <item><c>--spike-smoke &lt;folder&gt;</c> runs the drag detection log for a few seconds and saves its log.</item>
/// <item><c>--drop-test &lt;folder&gt;</c> drops generated files on the wheel through OLE and checks the conversions.</item>
/// </list>
/// </summary>
internal static class DebugCommands
{
    public static bool IsDebugCommand(string[] args) => args is ["--render-ui", _] or ["--spike-smoke", _] or ["--drop-test", _];

    public static int Run(string[] args) => args switch
    {
        ["--render-ui", var folder] => UiSnapshots.RenderAll(folder),
        ["--spike-smoke", var folder] => SpikeSmokeTest.Run(folder),
        ["--drop-test", var folder] => DropTest.Run(folder),
        _ => 2,
    };
}
#endif
