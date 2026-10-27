using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Sulap.App.Platform;
using Sulap.Core;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Spike;

/// <summary>Facts about this machine that explain how drags behave on it.</summary>
internal static class SpikeEnvironment
{
    public static string Describe()
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{AppIdentity.Name} {AppIdentity.Version} drag detection log, {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Windows: {WindowsVersion()}, {RuntimeInformation.OSArchitecture}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Process: {RuntimeInformation.ProcessArchitecture}, {(ProcessInfo.IsElevated ? "ELEVATED" : "not elevated")}, DPI awareness: {DpiAwareness()}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Drag threshold (SM_CXDRAG x SM_CYDRAG): {GetSystemMetrics(SM_CXDRAG)} x {GetSystemMetrics(SM_CYDRAG)} px; mouse buttons swapped: {YesNo(GetSystemMetrics(SM_SWAPBUTTON) != 0)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Animation effects: {(SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var animations, 0) ? (animations != 0 ? "on" : "off") : "unknown")}; LowLevelHooksTimeout: {LowLevelHooksTimeout()}");
        foreach (var monitor in Monitors())
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Monitor: {monitor}");
        }

        return text.ToString().TrimEnd();
    }

    private static string WindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = Environment.OSVersion.Version.Build;
        var name = build >= 22000 ? "Windows 11" : "Windows 10";
        var displayVersion = key?.GetValue("DisplayVersion") as string ?? "?";
        var revision = key?.GetValue("UBR") is int ubr ? ubr : 0;
        var edition = key?.GetValue("EditionID") as string ?? "?";
        return $"{name} {edition} {displayVersion} (build {build}.{revision})";
    }

    private static string DpiAwareness()
    {
        var context = GetThreadDpiAwarenessContext();
        return AreDpiAwarenessContextsEqual(context, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) ? "per-monitor v2" : "NOT per-monitor v2";
    }

    private static string LowLevelHooksTimeout()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("LowLevelHooksTimeout")?.ToString() is { } value ? $"{value} ms" : "not set";
    }

    private static List<string> Monitors()
    {
        var monitors = new List<string>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(handle, ref info);
            var dpi = GetDpiForMonitor(handle, MDT_EFFECTIVE_DPI, out var x, out _) == 0 ? x : 0;
            var bounds = info.rcMonitor;
            var work = info.rcWork;
            monitors.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} at ({bounds.Left},{bounds.Top}), work area {work.Right - work.Left}x{work.Bottom - work.Top} at ({work.Left},{work.Top}), {dpi * 100 / 96}% ({dpi} dpi){((info.dwFlags & MONITORINFOF_PRIMARY) != 0 ? ", primary" : string.Empty)}"));
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
}
