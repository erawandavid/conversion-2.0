using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Sulap.Core.Gestures;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

internal enum MouseButton
{
    None,
    Left,
    Right,
}

internal enum MouseHookEventKind
{
    Pressed,
    DragStarted,
    Released,
}

internal readonly record struct MouseHookEvent(
    MouseHookEventKind Kind, MouseButton Button, int X, int Y, bool IsInjected, long Timestamp);

/// <summary>
/// A system-wide WH_MOUSE_LL hook on its own thread. The callback only tracks the pressed button and
/// the drag threshold, and reports changes: Windows silently removes hooks that answer slowly, and a
/// slow hook makes the pointer lag everywhere.
/// </summary>
internal sealed class LowLevelMouseHook : IDisposable
{
    private static readonly long SlowCallbackTicks = Stopwatch.Frequency / 1000;

    private readonly LowLevelHookProc _callback;
    private readonly DragThreshold _threshold;
    private readonly MessageLoopThread _thread;
    private IntPtr _hook;
    private MouseButton _pressed;
    private POINT _pressPoint;
    private bool _dragging;
    private long _callbacks;
    private long _slowCallbacks;
    private long _slowestTicks;

    public LowLevelMouseHook(DragThreshold threshold)
    {
        _threshold = threshold;
        _callback = OnHook; // Kept in a field so the delegate outlives the native hook.
        _thread = new MessageLoopThread("Mouse hook", ThreadPriority.Highest, Install, Uninstall);
    }

    /// <summary>Raised on the hook thread. Handlers must return quickly.</summary>
    public event Action<MouseHookEvent>? Changed;

    public long Callbacks => Volatile.Read(ref _callbacks);

    /// <summary>Callbacks that took longer than 1 ms.</summary>
    public long SlowCallbacks => Volatile.Read(ref _slowCallbacks);

    public TimeSpan Slowest => TimeSpan.FromSeconds(Volatile.Read(ref _slowestTicks) / (double)Stopwatch.Frequency);

    public void Dispose() => _thread.Dispose();

    private void Install()
    {
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception();
        }
    }

    private void Uninstall() => UnhookWindowsHookEx(_hook);

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                // Read MSLLHOOKSTRUCT's fields in place (pt.x, pt.y, flags) instead of copying it to the heap.
                var point = new POINT { X = Marshal.ReadInt32(lParam, 0), Y = Marshal.ReadInt32(lParam, 4) };
                var flags = (uint)Marshal.ReadInt32(lParam, 12);
                Track((int)wParam, point, flags, started);
            }
            catch (Exception)
            {
                // An exception must never travel back into Windows' input processing.
            }

            Measure(Stopwatch.GetTimestamp() - started);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Track(int message, POINT point, uint flags, long timestamp)
    {
        switch (message)
        {
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN when _pressed == MouseButton.None:
                _pressed = message == WM_LBUTTONDOWN ? MouseButton.Left : MouseButton.Right;
                _pressPoint = point;
                _dragging = false;
                Raise(MouseHookEventKind.Pressed, point, flags, timestamp);
                break;

            case WM_MOUSEMOVE when _pressed != MouseButton.None && !_dragging:
                if (_threshold.IsExceeded(point.X - _pressPoint.X, point.Y - _pressPoint.Y))
                {
                    _dragging = true;
                    Raise(MouseHookEventKind.DragStarted, point, flags, timestamp);
                }

                break;

            case WM_LBUTTONUP when _pressed == MouseButton.Left:
            case WM_RBUTTONUP when _pressed == MouseButton.Right:
                Raise(MouseHookEventKind.Released, point, flags, timestamp);
                _pressed = MouseButton.None;
                _dragging = false;
                break;
        }
    }

    private void Raise(MouseHookEventKind kind, POINT point, uint flags, long timestamp) =>
        Changed?.Invoke(new MouseHookEvent(kind, _pressed, point.X, point.Y, (flags & LLMHF_INJECTED) != 0, timestamp));

    private void Measure(long ticks)
    {
        // Only the hook thread writes these; other threads just read them.
        Volatile.Write(ref _callbacks, _callbacks + 1);
        if (ticks > SlowCallbackTicks)
        {
            Volatile.Write(ref _slowCallbacks, _slowCallbacks + 1);
        }

        if (ticks > _slowestTicks)
        {
            Volatile.Write(ref _slowestTicks, ticks);
        }
    }
}
