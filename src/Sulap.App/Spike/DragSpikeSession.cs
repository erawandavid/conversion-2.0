using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Sulap.App.DragDetection;
using Sulap.Core.Gestures;
using Sulap.Core.Settings;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Spike;

/// <summary>
/// Phase 1 spike: runs every drag signal at once and logs what each one sees, so the real
/// detection (Phase 2) can be built on the signals that work on the owner's machine.
/// Hook callbacks only update a few fields and queue log lines; anything touching windows, and
/// every decision about when a drag is over (<see cref="DragEndTracker"/>), runs on the UI thread.
/// </summary>
internal sealed class DragSpikeSession : IDisposable
{
    private const string DragImageClass = "SysDragImage";
    private static readonly TimeSpan DragEnterTimeout = TimeSpan.FromSeconds(1.5);

    private readonly SpikeLog _log;
    private readonly Dispatcher _ui;
    private readonly WheelTrigger _trigger;
    private readonly SensorWindow _sensor = new();
    private readonly object _gate = new();
    private readonly Dictionary<IntPtr, uint> _dragImages = []; // Guarded by _gate.
    private readonly HashSet<string> _classesSeenThisPress = []; // Window-event thread only.
    private LowLevelMouseHook? _mouse;
    private WindowEventWatcher? _windows;
    private KeyStatePoller? _keys;
    private volatile bool _buttonDown;
    private bool _polledLeftDown; // Key-polling thread only.
    private bool _polledRightDown; // Key-polling thread only.
    private volatile bool _mouseDragging;
    private int _pressNumber;
    private int _classesSeenForPress = -1;

    // UI thread only.
    private readonly DragEndTracker _endTracker = new();
    private readonly DispatcherTimer _graceTimer;
    private long _sensorShownAt;
    private bool _sensorGotDragEnter;
    private int _dragOverCount;
    private DragDropEffects _lastDragOverEffect;
    private SpikeStats _stats;

    public DragSpikeSession(SpikeLog log, TriggerKeys triggerKeys)
    {
        _log = log;
        _ui = Dispatcher.CurrentDispatcher;
        _trigger = new WheelTrigger(triggerKeys);
        _graceTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui);
        _graceTimer.Tick += OnGraceTimerTick;
        _sensor.DragEnter += OnSensorDragEnter;
        _sensor.DragOver += OnSensorDragOver;
        _sensor.DragLeave += OnSensorDragLeave;
        _sensor.Drop += OnSensorDrop;
    }

    public bool TintSensor { get; set; } = true;

    public bool KeepDragImageOnTop { get; set; } = true;

    public bool NudgeMouse { get; set; } = true;

    public string HookStatus { get; private set; } = string.Empty;

    public void Start()
    {
        _log.WriteRaw(SpikeEnvironment.Describe());
        _log.WriteRaw(string.Empty);

        var threshold = new DragThreshold(GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG));
        _mouse = TryStart("mouse hook (WH_MOUSE_LL)", () => new LowLevelMouseHook(threshold));
        _windows = TryStart("window events (SetWinEventHook, out of context)", () => new WindowEventWatcher());
        _keys = TryStart("key polling (GetAsyncKeyState, about 60 Hz)", () => new KeyStatePoller());
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

        HookStatus = $"Mouse hook {Check(_mouse)}   Window events {Check(_windows)}   Key polling {Check(_keys)}";
    }

    public void WriteSummary()
    {
        _log.WriteRaw(string.Empty);
        _log.WriteRaw("=== Summary ===");
        if (_mouse is not null)
        {
            _log.WriteRaw($"Mouse hook: {_mouse.Callbacks:N0} callbacks, slowest {_mouse.Slowest.TotalMilliseconds:0.000} ms, {_mouse.SlowCallbacks} over 1 ms");
        }

        if (_keys is not null)
        {
            _log.WriteRaw($"Key polling: {_keys.Polls:N0} polls, every {_keys.AverageInterval.TotalMilliseconds:0.0} ms on average, longest gap {_keys.LongestInterval.TotalMilliseconds:0.0} ms");
        }

        _log.WriteRaw($"Drag image windows seen: {_stats.DragImagesSeen}, replaced {_stats.DragImageReplacements} times"
            + (_stats.DragImageReplacements > 0 ? $" ({_stats.FastestReplacementMs:0} to {_stats.SlowestReplacementMs:0} ms after the old one was destroyed)" : string.Empty));
        _log.WriteRaw($"Sensor: shown {_stats.SensorShown} times, DragEnter {_stats.DragEnters} times"
            + (_stats.DragEnters > 0 ? $" ({_stats.FastestDragEnterMs:0} to {_stats.SlowestDragEnterMs:0} ms after showing)" : string.Empty)
            + $", no DragEnter {_stats.MissedDragEnters} times, drops {_stats.Drops}");
        _log.WriteRaw(string.Empty);
    }

    public void Dispose()
    {
        _graceTimer.Stop();
        _mouse?.Dispose();
        _windows?.Dispose();
        _keys?.Dispose();
        _sensor.Close();
    }

    private static string Check(object? component) => component is null ? "FAILED" : "OK";

    private T? TryStart<T>(string name, Func<T> start)
        where T : class
    {
        try
        {
            var component = start();
            _log.Write("setup", $"{name}: started");
            return component;
        }
        catch (Exception ex)
        {
            _log.Write("setup", $"{name}: FAILED ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // ---- Hook threads ----

    private void OnMouse(MouseHookEvent e)
    {
        var injected = e.IsInjected ? " (injected)" : string.Empty;
        switch (e.Kind)
        {
            case MouseHookEventKind.Pressed:
                Interlocked.Increment(ref _pressNumber);
                _buttonDown = true;
                _mouseDragging = false;
                _log.Write(e.Timestamp, "mouse", $"{e.Button} button down at ({e.X},{e.Y}){injected}");
                _ui.BeginInvoke(() => _endTracker.OnButtonPressed(ToTime(e.Timestamp)));
                break;
            case MouseHookEventKind.DragStarted:
                _mouseDragging = true;
                _log.Write(e.Timestamp, "mouse", $"moved past the drag threshold at ({e.X},{e.Y}){injected}");
                break;
            case MouseHookEventKind.Released:
                _buttonDown = false;
                _mouseDragging = false;
                _log.Write(e.Timestamp, "mouse", $"{e.Button} button up at ({e.X},{e.Y}){injected}");
                _ui.BeginInvoke(() => EndIfOver(_endTracker.OnButtonReleased(ToTime(e.Timestamp))));
                break;
        }
    }

    private void OnWindow(WindowEvent e)
    {
        bool isDragImage;
        lock (_gate)
        {
            isDragImage = e.ClassName == DragImageClass || _dragImages.ContainsKey(e.Handle);
            if (isDragImage && e.Kind == WindowEventKind.Destroyed)
            {
                _dragImages.Remove(e.Handle);
            }
            else if (isDragImage)
            {
                _dragImages[e.Handle] = e.ProcessId;
            }
        }

        if (isDragImage)
        {
            var style = e.Kind == WindowEventKind.Destroyed ? string.Empty : $", {DescribeStyle(e.Handle)}";
            _log.Write(e.Timestamp, "window", $"{DragImageClass} {e.Kind.ToString().ToLowerInvariant()} (0x{e.Handle:X}, {_log.ProcessName(e.ProcessId)}{style})");
            _ui.BeginInvoke(() => OnDragImageEvent(e.Kind, e.Handle, e.Timestamp));
            return;
        }

        // To discover other signals, list the other top-level windows that appear while a button is held.
        if (!_buttonDown || e.Kind is not (WindowEventKind.Created or WindowEventKind.Shown) || e.ClassName.Length == 0)
        {
            return;
        }

        var press = Volatile.Read(ref _pressNumber);
        if (press != _classesSeenForPress)
        {
            _classesSeenForPress = press;
            _classesSeenThisPress.Clear();
        }

        if (_classesSeenThisPress.Count < 12 && _classesSeenThisPress.Add(e.ClassName))
        {
            _log.Write(e.Timestamp, "window", $"other window {e.Kind.ToString().ToLowerInvariant()} while the button is down: \"{e.ClassName}\" ({_log.ProcessName(e.ProcessId)})");
        }
    }

    private void OnKey(KeyChange e)
    {
        if (e.Key == PolledKey.LeftButton)
        {
            _polledLeftDown = e.IsDown;
        }
        else if (e.Key == PolledKey.RightButton)
        {
            _polledRightDown = e.IsDown;
        }

        // Typing while no mouse button is held has nothing to do with drags; keep it out of the log.
        var duringPress = _buttonDown || _polledLeftDown || _polledRightDown;
        if (duringPress || e.Key is PolledKey.LeftButton or PolledKey.RightButton)
        {
            _log.Write(e.Timestamp, "key", $"{e.Key} {(e.IsDown ? "down" : "up")} (polled)");
        }

        if (!e.IsDown)
        {
            return;
        }

        if (e.Key == PolledKey.Esc)
        {
            _ui.BeginInvoke(() => EndIfOver(_endTracker.OnEscape(ToTime(e.Timestamp))));
            return;
        }

        var key = e.Key switch
        {
            PolledKey.Shift => TriggerKey.Shift,
            PolledKey.Ctrl => TriggerKey.Ctrl,
            PolledKey.Alt => TriggerKey.Alt,
            _ => TriggerKey.None,
        };
        if (key == TriggerKey.None)
        {
            return;
        }

        var dragImage = VisibleDragImage();
        var mouseDragging = _mouseDragging;
        if (dragImage == IntPtr.Zero && !mouseDragging)
        {
            if (duringPress)
            {
                _log.Write("decision", $"{key} ignored: a button is down but no drag is in progress");
            }

            return;
        }

        _ui.BeginInvoke(() => OnTriggerKey(key, dragImage, mouseDragging, e.Timestamp));
    }

    private IntPtr VisibleDragImage()
    {
        lock (_gate)
        {
            foreach (var handle in _dragImages.Keys)
            {
                if (IsWindowVisible(handle))
                {
                    return handle;
                }
            }
        }

        return IntPtr.Zero;
    }

    private static TimeSpan ToTime(long timestamp) => Stopwatch.GetElapsedTime(0, timestamp);

    private static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    // ---- UI thread ----

    private void OnDragImageEvent(WindowEventKind kind, IntPtr handle, long timestamp)
    {
        var at = ToTime(timestamp);
        TimeSpan? replacedAfter = null;
        switch (kind)
        {
            case WindowEventKind.Created:
                replacedAfter = _endTracker.OnDragImageCreated(handle, at);
                break;
            case WindowEventKind.Shown:
                _stats.DragImagesSeen++;
                replacedAfter = _endTracker.OnDragImageShown(handle, at);
                break;
            case WindowEventKind.Hidden:
                _endTracker.OnDragImageHidden(handle);
                break;
            case WindowEventKind.Destroyed:
                // Not the end yet: File Explorer replaces its drag image when the pointer moves onto
                // another window, such as the sensor. The tracker decides after the grace period.
                _endTracker.OnDragImageDestroyed(handle, at);
                ScheduleGraceCheck();
                break;
        }

        if (replacedAfter is { } delay)
        {
            var milliseconds = delay.TotalMilliseconds;
            _stats.DragImageReplacements++;
            _stats.FastestReplacementMs = _stats.DragImageReplacements == 1 ? milliseconds : Math.Min(_stats.FastestReplacementMs, milliseconds);
            _stats.SlowestReplacementMs = Math.Max(_stats.SlowestReplacementMs, milliseconds);
            _log.Write(timestamp, "window", $"drag image replaced after {milliseconds:0} ms");
        }
    }

    private void ScheduleGraceCheck()
    {
        if (_endTracker.GraceCheckDue is not { } due)
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
        _graceTimer.Stop();
        var reason = _endTracker.CheckGracePeriod(ToTime(Stopwatch.GetTimestamp()), IsAnyMouseButtonDown());
        if (reason is not null)
        {
            EndIfOver(reason);
        }
        else if (_endTracker.GraceCheckDue is not null)
        {
            ScheduleGraceCheck(); // The timer fired a little early.
        }
        else if (_endTracker.IsActive)
        {
            var grace = _endTracker.GracePeriod.TotalMilliseconds;
            _log.Write("decision", _endTracker.HasVisibleDragImage
                ? $"{grace:0} ms after a {DragImageClass} was destroyed, one is visible again: the drag goes on"
                : $"no {DragImageClass} for {grace:0} ms, but a mouse button is still down: the drag goes on");
        }
    }

    private void EndIfOver(DragEndReason? reason)
    {
        if (reason is not { } ending)
        {
            return;
        }

        var description = ending switch
        {
            DragEndReason.MouseReleased => "the mouse button was released",
            DragEndReason.Escape => "Esc was pressed",
            _ => $"no {DragImageClass} for {_endTracker.GracePeriod.TotalMilliseconds:0} ms and no mouse button down",
        };
        _graceTimer.Stop();
        _log.Write("decision", $"drag over: {description}");
        _trigger.Reset();
        HideSensor(description);
    }

    private void OnTriggerKey(TriggerKey key, IntPtr dragImage, bool mouseDragging, long keyTimestamp)
    {
        var signals = $"drag image: {(dragImage != IntPtr.Zero ? "yes" : "no")}, mouse past threshold: {(mouseDragging ? "yes" : "no")}";
        if (!_trigger.OnKeyDown(key))
        {
            _log.Write("decision", $"{key}: the {_trigger.Current} wheel stays ({signals})");
            return;
        }

        _log.Write("decision", $"{key}: would open the {_trigger.Current} wheel ({signals})");
        if (!_sensor.IsVisible)
        {
            ShowSensor(dragImage, keyTimestamp);
        }
    }

    private void ShowSensor(IntPtr dragImage, long keyTimestamp)
    {
        GetCursorPos(out var cursor);
        _sensor.IsTinted = TintSensor;
        var insertAfter = KeepDragImageOnTop ? dragImage : IntPtr.Zero;
        var rect = _sensor.ShowCenteredAt(cursor, insertAfter);
        _sensorShownAt = Stopwatch.GetTimestamp();
        _sensorGotDragEnter = false;
        _dragOverCount = 0;
        _lastDragOverEffect = DragDropEffects.Scroll; // Anything that DragOver never sets.
        _stats.SensorShown++;

        _log.Write("sensor", $"shown {_log.MillisecondsSince(keyTimestamp):0} ms after the key, at ({rect.Left},{rect.Top}) {rect.Right - rect.Left}x{rect.Bottom - rect.Top} px; "
            + $"window above it: {DescribeWindowAbove(_sensor.Handle)}; {DescribeStyle(_sensor.Handle)}");

        if (NudgeMouse)
        {
            var input = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } } };
            var sent = SendInput(1, input, Marshal.SizeOf<INPUT>());
            _log.Write("sensor", sent == 1 ? "nudged the mouse (a move of 0,0)" : $"nudging the mouse FAILED (error {Marshal.GetLastWin32Error()})");
        }

        var shownAt = _sensorShownAt;
        var timer = new DispatcherTimer { Interval = DragEnterTimeout };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_sensor.IsVisible && !_sensorGotDragEnter && _sensorShownAt == shownAt)
            {
                _stats.MissedDragEnters++;
                _log.Write("sensor", $"no DragEnter within {DragEnterTimeout.TotalSeconds} s: not a drag-and-drop operation, or the pointer hasn't moved yet");
            }
        };
        timer.Start();
    }

    private void HideSensor(string reason)
    {
        if (!_sensor.IsVisible)
        {
            return;
        }

        _sensor.Hide();
        _log.Write("sensor", $"hidden: {reason}");
    }

    private void OnSensorDragEnter(object sender, DragEventArgs e)
    {
        _sensorGotDragEnter = true;
        var delay = _log.MillisecondsSince(_sensorShownAt);
        _stats.DragEnters++;
        _stats.FastestDragEnterMs = _stats.DragEnters == 1 ? delay : Math.Min(_stats.FastestDragEnterMs, delay);
        _stats.SlowestDragEnterMs = Math.Max(_stats.SlowestDragEnterMs, delay);

        var formats = e.Data.GetFormats(autoConvert: false);
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false);
        var virtualFiles = formats.Any(format => format.StartsWith("FileGroupDescriptor", StringComparison.Ordinal));
        _log.Write("sensor", $"DragEnter {delay:0} ms after showing; real files (FileDrop): {(hasFiles ? "yes" : "no")}; virtual files: {(virtualFiles ? "yes" : "no")}; "
            + $"keys: {e.KeyStates}; allowed effects: {e.AllowedEffects}; formats: {string.Join(", ", formats)}");

        e.Effects = EffectFor(e);
        e.Handled = true;
        if (hasFiles)
        {
            _log.Write("decision", $"files: would animate the {_trigger.Current} wheel in");
        }
        else
        {
            _log.Write("decision", "not files: hide the sensor so the drop goes where it normally would");
            HideSensor("not a file drag");
        }
    }

    private void OnSensorDragOver(object sender, DragEventArgs e)
    {
        _dragOverCount++;
        e.Effects = EffectFor(e);
        e.Handled = true;
        if (e.Effects != _lastDragOverEffect)
        {
            _lastDragOverEffect = e.Effects;
            _log.Write("sensor", $"DragOver answers {e.Effects} (keys: {e.KeyStates})");
        }
    }

    private void OnSensorDragLeave(object sender, DragEventArgs e)
    {
        _log.Write("sensor", $"DragLeave after {_dragOverCount} DragOver calls");
    }

    private void OnSensorDrop(object sender, DragEventArgs e)
    {
        var started = Stopwatch.GetTimestamp();
        var files = e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];
        var readMs = _log.MillisecondsSince(started);
        e.Effects = files.Length > 0 ? EffectFor(e) : DragDropEffects.None;
        e.Handled = true;
        _stats.Drops++;

        var extensions = string.Join(", ", files.Select(file => Path.GetExtension(file).ToLowerInvariant()).Distinct());
        var inTemp = files.Any(file => file.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
        _log.Write("sensor", $"Drop: {files.Length} file(s) ({extensions}){(inTemp ? ", inside the TEMP folder" : string.Empty)}; keys: {e.KeyStates}; "
            + $"answered {e.Effects}; reading the paths took {readMs:0.0} ms; nothing was done with the files");
        HideSensor("dropped");
    }

    /// <summary>
    /// Copy when the source allows it, otherwise nothing. Never Move: Shift means "move" in File
    /// Explorer, and a target that reports Move can make Explorer delete the original.
    /// </summary>
    private static DragDropEffects EffectFor(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false) && e.AllowedEffects.HasFlag(DragDropEffects.Copy)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private static string DescribeWindowAbove(IntPtr handle)
    {
        var above = GetWindow(handle, GW_HWNDPREV);
        if (above == IntPtr.Zero)
        {
            return "none (it's at the top)";
        }

        var name = new char[256];
        var length = GetClassName(above, name, name.Length);
        return $"\"{new string(name, 0, Math.Max(0, length))}\"";
    }

    private static string DescribeStyle(IntPtr handle)
    {
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        var flags = new List<string>();
        if ((style & WS_EX_TOPMOST) != 0)
        {
            flags.Add("topmost");
        }

        if ((style & WS_EX_LAYERED) != 0)
        {
            flags.Add("layered");
        }

        if ((style & WS_EX_TRANSPARENT) != 0)
        {
            flags.Add("click-through");
        }

        if ((style & WS_EX_NOACTIVATE) != 0)
        {
            flags.Add("no-activate");
        }

        return flags.Count == 0 ? "no special styles" : string.Join(", ", flags);
    }

    private struct SpikeStats
    {
        public int DragImagesSeen;
        public int DragImageReplacements;
        public double FastestReplacementMs;
        public double SlowestReplacementMs;
        public int SensorShown;
        public int DragEnters;
        public int MissedDragEnters;
        public int Drops;
        public double FastestDragEnterMs;
        public double SlowestDragEnterMs;
    }
}
