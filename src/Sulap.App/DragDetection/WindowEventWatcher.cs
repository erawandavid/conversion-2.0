using System.Diagnostics;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

internal enum WindowEventKind
{
    Created,
    Destroyed,
    Shown,
    Hidden,
}

/// <param name="ClassName">Empty for <see cref="WindowEventKind.Destroyed"/>: the window is gone by then.</param>
internal readonly record struct WindowEvent(WindowEventKind Kind, IntPtr Handle, string ClassName, uint ProcessId, long Timestamp);

/// <summary>
/// Reports top-level windows appearing and disappearing anywhere on the desktop, through an
/// out-of-context WinEvent hook (EVENT_OBJECT_CREATE to EVENT_OBJECT_HIDE) on its own thread.
/// Out of context means Windows queues the events to us; it never waits for this process.
/// </summary>
internal sealed class WindowEventWatcher : IDisposable
{
    private readonly WinEventProc _callback;
    private readonly char[] _className = new char[256];
    private readonly MessageLoopThread _thread;
    private IntPtr _hook;

    public WindowEventWatcher()
    {
        _callback = OnEvent; // Kept in a field so the delegate outlives the native hook.
        _thread = new MessageLoopThread("Window events", ThreadPriority.AboveNormal, Install, Uninstall);
    }

    /// <summary>Raised on the watcher thread.</summary>
    public event Action<WindowEvent>? Changed;

    public void Dispose() => _thread.Dispose();

    private void Install()
    {
        _hook = SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException("SetWinEventHook failed.");
        }
    }

    private void Uninstall() => UnhookWinEvent(_hook);

    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        // Most events are about other objects (the caret, the cursor, child controls); skip them first.
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF || hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var timestamp = Stopwatch.GetTimestamp();
            var kind = eventType switch
            {
                EVENT_OBJECT_CREATE => WindowEventKind.Created,
                EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
                EVENT_OBJECT_SHOW => WindowEventKind.Shown,
                _ => WindowEventKind.Hidden,
            };

            var className = string.Empty;
            if (kind != WindowEventKind.Destroyed)
            {
                if (GetAncestor(hwnd, GA_ROOT) != hwnd)
                {
                    return;
                }

                var length = GetClassName(hwnd, _className, _className.Length);
                className = length > 0 ? new string(_className, 0, length) : string.Empty;
            }

            GetWindowThreadProcessId(hwnd, out var processId);
            Changed?.Invoke(new WindowEvent(kind, hwnd, className, processId, timestamp));
        }
        catch (Exception)
        {
            // Never let an exception escape into the native callback.
        }
    }
}
