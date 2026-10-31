using System.Diagnostics;
using System.Windows.Threading;
using Sulap.Core.Gestures;
using Sulap.Core.Settings;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.DragDetection;

/// <summary>
/// Watches drags anywhere on the desktop and tells the UI thread when a trigger key goes down during
/// one (<see cref="WheelRequested"/>) and when the drag is over (<see cref="DragEnded"/>). It uses
/// the signals the Phase 1 spike found reliable: the shell's drag image window, a low-level mouse
/// hook for the press and the drag threshold, and polled key states, because the dragging app's
/// modal loop owns the keyboard during a drag. Hook callbacks only update a few fields and post to
/// the UI thread; every decision, including <see cref="DragEndTracker"/>'s, is made there.
/// </summary>
internal sealed class DragMonitor(TriggerKeys triggerKeys) : IDisposable
{
    private const string DragImageClass = "SysDragImage";

    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly object _gate = new();
    private readonly HashSet<IntPtr> _dragImages = []; // Guarded by _gate.
    private LowLevelMouseHook? _mouse;
    private WindowEventWatcher? _windows;
    private volatile KeyStatePoller? _keys; // Read by the hook threads to wake it.
    private volatile bool _mouseDragging;
    private volatile bool _usePolledButtons;

    // UI thread only.
    private readonly WheelTrigger _trigger = new(triggerKeys);
    private DragEndTracker _endTracker = new();
    private DispatcherTimer? _graceTimer;

    /// <summary>
    /// A trigger key went down during a drag. Raised on the UI thread, with the shell's drag image
    /// window when one is visible (the wheel goes just below it, so the file's thumbnail stays on top).
    /// </summary>
    public event Action<WheelKind, IntPtr>? WheelRequested;

    /// <summary>The drag is over. Raised on the UI thread.</summary>
    public event Action<DragEndReason>? DragEnded;

    public bool IsRunning { get; private set; }

    /// <summary>The signals that didn't start, for the log. Detection still works with the others.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        var problems = new List<string>();
        var threshold = new DragThreshold(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG));
        _mouse = TryStart("mouse hook", () => new LowLevelMouseHook(threshold), problems);
        _windows = TryStart("window events", () => new WindowEventWatcher(), problems);
        // With the mouse hook to wake it, key polling sleeps whenever no mouse button is down.
        var gated = _mouse is not null;
        _keys = TryStart("key polling", () => new KeyStatePoller(onlyWhileButtonDown: gated), problems);

        // Without the mouse hook, polled button states stand in for presses and releases.
        _usePolledButtons = _mouse is null;
        _endTracker = new DragEndTracker();
        _graceTimer ??= CreateGraceTimer();
        if (_mouse is not null)
        {
            _mouse.Changed += OnMouse;
        }

        if (_windows is not null)
        {
            _windows.Changed += OnWindow;
        }

        if (_keys is not null)
        {
            _keys.Changed += OnKey;
        }

        Problems = problems;
        IsRunning = true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;
        _graceTimer?.Stop();
        _mouse?.Dispose();
        _windows?.Dispose();
        _keys?.Dispose();
        _mouse = null;
        _windows = null;
        _keys = null;
        _mouseDragging = false;
        lock (_gate)
        {
            _dragImages.Clear();
        }

        _trigger.Reset();
    }

    public void Dispose() => Stop();

    private static T? TryStart<T>(string name, Func<T> start, List<string> problems)
        where T : class
    {
        try
        {
            return start();
        }
        catch (Exception ex)
        {
            problems.Add($"{name} didn't start ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private DispatcherTimer CreateGraceTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
        timer.Tick += OnGraceTimerTick;
        return timer;
    }

    private static TimeSpan ToTime(long timestamp) => Stopwatch.GetElapsedTime(0, timestamp);

    private static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    private void Post(Action action) => _ui.BeginInvoke(action);

    // ---- Hook threads: update fields, post to the UI thread, return. ----

    private void OnMouse(MouseHookEvent e)
    {
        switch (e.Kind)
        {
            case MouseHookEventKind.Pressed:
                _mouseDragging = false;
                _keys?.Wake();
                Post(() => OnButtonPressed(e.Timestamp));
                break;
            case MouseHookEventKind.DragStarted:
                _mouseDragging = true;
                _keys?.Wake(); // Again, in case the press came before the button state did.
                break;
            case MouseHookEventKind.Released:
                _mouseDragging = false;
                Post(() => OnButtonReleased(e.Timestamp));
                break;
        }
    }

    private void OnWindow(WindowEvent e)
    {
        lock (_gate)
        {
            // A destroyed window has no class name any more; it's known by its handle.
            if (e.ClassName != DragImageClass && !_dragImages.Contains(e.Handle))
            {
                return;
            }

            if (e.Kind == WindowEventKind.Destroyed)
            {
                _dragImages.Remove(e.Handle);
            }
            else
            {
                _dragImages.Add(e.Handle);
            }
        }

        if (e.Kind == WindowEventKind.Shown)
        {
            _keys?.Wake();
        }

        Post(() => OnDragImageEvent(e.Kind, e.Handle, e.Timestamp));
    }

    private void OnKey(KeyChange e)
    {
        if (e.Key is PolledKey.LeftButton or PolledKey.RightButton)
        {
            if (_usePolledButtons)
            {
                Post(() =>
                {
                    if (e.IsDown)
                    {
                        OnButtonPressed(e.Timestamp);
                    }
                    else
                    {
                        OnButtonReleased(e.Timestamp);
                    }
                });
            }

            return;
        }

        if (!e.IsDown)
        {
            return; // The wheel stays open after the key comes back up.
        }

        if (e.Key == PolledKey.Esc)
        {
            Post(() => End(_endTracker.OnEscape(ToTime(e.Timestamp))));
            return;
        }

        var key = e.Key switch
        {
            PolledKey.Shift => TriggerKey.Shift,
            PolledKey.Ctrl => TriggerKey.Ctrl,
            PolledKey.Alt => TriggerKey.Alt,
            _ => TriggerKey.None,
        };
        var dragImage = VisibleDragImage();
        if (key == TriggerKey.None || (dragImage == IntPtr.Zero && !_mouseDragging))
        {
            return; // Typing, or a key pressed while nothing is being dragged.
        }

        Post(() => OnTriggerKey(key, dragImage));
    }

    private IntPtr VisibleDragImage()
    {
        lock (_gate)
        {
            foreach (var handle in _dragImages)
            {
                if (IsWindowVisible(handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    // ---- UI thread ----

    private void OnButtonPressed(long timestamp)
    {
        if (IsRunning)
        {
            _endTracker.OnButtonPressed(ToTime(timestamp));
        }
    }

    private void OnButtonReleased(long timestamp)
    {
        if (IsRunning)
        {
            End(_endTracker.OnButtonReleased(ToTime(timestamp)));
        }
    }

    private void OnTriggerKey(TriggerKey key, IntPtr dragImage)
    {
        // The drag may have ended between the key press and now; then this key opens nothing.
        if (!IsRunning || !_endTracker.IsActive || !_trigger.OnKeyDown(key) || _trigger.Current is not { } kind)
        {
            return;
        }

        try
        {
            WheelRequested?.Invoke(kind, dragImage);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Opening the {kind} wheel failed: {ex}");
        }
    }

    private void OnDragImageEvent(WindowEventKind kind, IntPtr handle, long timestamp)
    {
        if (!IsRunning)
        {
            return;
        }

        var at = ToTime(timestamp);
        switch (kind)
        {
            case WindowEventKind.Created:
                _endTracker.OnDragImageCreated(handle, at);
                break;
            case WindowEventKind.Shown:
                _endTracker.OnDragImageShown(handle, at);
                break;
            case WindowEventKind.Hidden:
                _endTracker.OnDragImageHidden(handle);
                break;
            case WindowEventKind.Destroyed:
                // Not the end yet: File Explorer replaces its drag image when the pointer moves onto
                // another window, such as the wheel. The tracker decides after the grace period.
                _endTracker.OnDragImageDestroyed(handle, at);
                ScheduleGraceCheck();
                break;
        }
    }

    private void ScheduleGraceCheck()
    {
        if (_graceTimer is null || _endTracker.GraceCheckDue is not { } due)
        {
            return;
        }

        var wait = due - ToTime(Stopwatch.GetTimestamp());
        _graceTimer.Stop();
        _graceTimer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _graceTimer.Start();
    }

    private void OnGraceTimerTick(object? sender, EventArgs e)
    {
        _graceTimer?.Stop();
        if (!IsRunning)
        {
            return;
        }

        var reason = _endTracker.CheckGracePeriod(ToTime(Stopwatch.GetTimestamp()), IsAnyMouseButtonDown());
        if (reason is not null)
        {
            End(reason);
        }
        else if (_endTracker.GraceCheckDue is not null)
        {
            ScheduleGraceCheck(); // The timer fired a little early.
        }
    }

    private void End(DragEndReason? reason)
    {
        if (!IsRunning || reason is not { } ending)
        {
            return;
        }

        _graceTimer?.Stop();
        _trigger.Reset();
        try
        {
            DragEnded?.Invoke(ending);
        }
        catch (Exception ex)
        {
            AppLog.Write($"Closing the wheel failed: {ex}");
        }
    }
}
