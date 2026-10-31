using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Wheel;

/// <summary>
/// The wheel's window: topmost, never activated, out of the taskbar and Alt+Tab, with per-pixel
/// transparency so that only what <see cref="WheelView"/> draws catches the drag.
/// </summary>
internal sealed class WheelWindow : Window
{
    public WheelWindow()
    {
        Title = string.Empty;
        Width = WheelView.WindowSize;
        Height = WheelView.WindowSize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        AllowDrop = true;
        Focusable = false;
        Content = View;
    }

    public WheelView View { get; } = new();

    public IntPtr Handle { get; private set; }

    /// <summary>
    /// Shows the window centered on a point in physical pixels, kept inside that monitor's work area.
    /// </summary>
    /// <param name="insertAfter">The window to sit just below in the z-order (the drag image), or zero for the top.</param>
    public void ShowCenteredAt(POINT point, IntPtr insertAfter)
    {
        Handle = new WindowInteropHelper(this).EnsureHandle();

        var monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) != 0)
        {
            dpi = 96;
        }

        var size = (int)Math.Round(Width * dpi / 96);
        var work = info.rcWork;
        var x = Math.Clamp(point.X - (size / 2), work.Left, Math.Max(work.Left, work.Right - size));
        var y = Math.Clamp(point.Y - (size / 2), work.Top, Math.Max(work.Top, work.Bottom - size));
        var zOrder = insertAfter == IntPtr.Zero ? HWND_TOPMOST : insertAfter;

        SetWindowPos(Handle, HWND_TOPMOST, x, y, size, size, SWP_NOACTIVATE);
        if (!IsVisible)
        {
            Show();
        }

        // Showing may have rescaled the window for the monitor's DPI; place it once more.
        SetWindowPos(Handle, zOrder, x, y, size, size, SWP_NOACTIVATE);
    }

    /// <summary>A point in <see cref="View"/>'s coordinates, in physical screen pixels.</summary>
    public POINT ToScreen(Point viewPoint)
    {
        var screen = View.PointToScreen(viewPoint);
        return new POINT { X = (int)Math.Round(screen.X), Y = (int)Math.Round(screen.Y) };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        // Never take the focus from the app the user drags from, and stay out of Alt+Tab.
        var style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}
