using System.Runtime.InteropServices;

namespace Sulap.App.Interop;

/// <summary>
/// Makes native popup menus (the tray menu) follow the Windows app theme. Windows has no public API
/// for this, so it uses the uxtheme exports that Windows itself and many apps rely on, available by
/// ordinal since Windows 10 1903. If any call fails, menus simply stay light.
/// </summary>
internal static class DarkMode
{
    private const int AllowDark = 1;

    private static readonly bool IsSupported = Environment.OSVersion.Version.Build >= 18362;

    public static void FollowAppsTheme() => TryCall(() =>
    {
        SetPreferredAppMode(AllowDark);
        FlushMenuThemes();
    });

    public static void AllowForWindow(IntPtr hwnd) => TryCall(() => AllowDarkModeForWindow(hwnd, true));

    /// <summary>Call after the theme changes (WM_SETTINGCHANGE) so the next menu picks it up.</summary>
    public static void Refresh() => TryCall(() =>
    {
        RefreshImmersiveColorPolicyState();
        FlushMenuThemes();
    });

    private static void TryCall(Action call)
    {
        if (!IsSupported)
        {
            return;
        }

        try
        {
            call();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#104")]
    private static extern void RefreshImmersiveColorPolicyState();

    [DllImport("uxtheme.dll", EntryPoint = "#133")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool AllowDarkModeForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.U1)] bool allow);

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
