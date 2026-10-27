using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

/// <summary>
/// A nearly invisible, never-activated, topmost window placed under the pointer. Windows only sends
/// it DragEnter when a real drag-and-drop operation passes over it, which tells a file drag apart
/// from a selection rectangle or a moving window.
/// </summary>
public partial class SensorWindow : Window
{
    public SensorWindow()
    {
        InitializeComponent();
    }

    public IntPtr Handle { get; private set; }

    /// <summary>Draws the sensor area as a blue circle so a tester can see where it is.</summary>
    public bool IsTinted
    {
        get => Tint.Visibility == Visibility.Visible;
        set => Tint.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the window centered on a point in physical pixels, kept inside that monitor's work area.
    /// </summary>
    /// <param name="insertAfter">The window to sit just below in the z-order, or zero for the top.</param>
    /// <returns>Where the window ended up, in physical pixels.</returns>
    internal RECT ShowCenteredAt(POINT point, IntPtr insertAfter)
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
        return new RECT { Left = x, Top = y, Right = x + size, Bottom = y + size };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        // Never take focus from the app the user drags from, and stay out of Alt+Tab.
        var style = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }
}
