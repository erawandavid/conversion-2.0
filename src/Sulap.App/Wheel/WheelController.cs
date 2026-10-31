using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Sulap.App.DragDetection;
using Sulap.App.Jobs;
using Sulap.Core;
using Sulap.Core.Conversion;
using Sulap.Core.Formats;
using Sulap.Core.Gestures;
using Sulap.Core.Settings;
using Sulap.Core.Wheels;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Wheel;

internal enum WheelState
{
    Hidden,

    /// <summary>Invisible under the pointer, waiting for DragEnter to say what's being dragged.</summary>
    Sensor,

    Open,
    Closing,
}

/// <summary>
/// Opens the Convert wheel when Shift goes down during a file drag, and turns a drop on a wedge into a
/// conversion. The wheel window first appears as an invisible sensor under the pointer: only a real
/// drag-and-drop operation sends it DragEnter, and that carries the dragged files, which decide the
/// wedges. Anything else (text, a selection rectangle, a window being moved) gets no wheel.
/// </summary>
internal sealed class WheelController : IDisposable
{
    // File Explorer makes its Drop call after the button comes up, so it can arrive just after the
    // mouse hook reported the release.
    private static readonly TimeSpan DropWait = TimeSpan.FromMilliseconds(500);

    private readonly DragMonitor _monitor;
    private readonly ConversionEngine _engine;
    private readonly ConversionJobs _jobs;
    private readonly bool _watchMouseButtons;
    private readonly WheelWindow _window = new();
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _dropWait;
    private int _buttonlessTicks;
    private bool _pointerOver;
    private bool _disposed;

    /// <param name="watchMouseButtons">
    /// Close the wheel when no mouse button is down, in case every drag-end signal was missed.
    /// Only the drop test turns this off: it drops without pressing a button.
    /// </param>
    public WheelController(TriggerKeys triggerKeys, ConversionEngine engine, ConversionJobs jobs, bool watchMouseButtons = true)
    {
        _engine = engine;
        _jobs = jobs;
        _watchMouseButtons = watchMouseButtons;
        _monitor = new DragMonitor(triggerKeys);
        _monitor.WheelRequested += OnWheelRequested;
        _monitor.DragEnded += OnDragEnded;
        _window.DragEnter += OnDragEnter;
        _window.DragOver += OnDragOver;
        _window.DragLeave += OnDragLeave;
        _window.Drop += OnDrop;
        _watchdog = new DispatcherTimer(DispatcherPriority.Normal, _window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _watchdog.Tick += OnWatchdogTick;
        _dropWait = new DispatcherTimer(DispatcherPriority.Normal, _window.Dispatcher) { Interval = DropWait };
        _dropWait.Tick += (_, _) => Close();
    }

    public WheelState State { get; private set; }

    public bool IsRunning => _monitor.IsRunning;

    internal IntPtr WindowHandle => _window.Handle;

    internal IReadOnlyList<ConvertOption> Options => _window.View.Shown?.Options ?? [];

    /// <summary>The open wheel is only a notice that the dragged files can't be converted.</summary>
    internal bool IsNotice => _window.View.Shown is { IsNotice: true };

    public void Start()
    {
        if (_disposed || _monitor.IsRunning)
        {
            return;
        }

        _monitor.Start();
        if (_monitor.Problems.Count > 0)
        {
            AppLog.Write($"Drag detection: {string.Join("; ", _monitor.Problems)}");
        }
    }

    public void Stop()
    {
        _monitor.Stop();
        HideNow();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _monitor.Dispose();
        _window.Close();
    }

    /// <summary>
    /// The wheel for these dragged files, or a notice saying why there's none; null only when there are
    /// no files at all. Built from the registry, so nothing here knows about particular formats.
    /// </summary>
    internal static WheelContent? Describe(IReadOnlyList<string> paths, FormatRegistry registry)
    {
        if (paths.Count == 0)
        {
            return null;
        }

        // The extension only: reading the files would be slow on OneDrive or a network drive, and the
        // converter checks their content anyway.
        var formats = paths.Select(FileFormats.FromPath).OfType<FileFormat>().ToList();
        IReadOnlyList<ConvertOption> options = formats.Count == 0 ? [] : registry.ConvertOptions(formats);
        if (options.Count == 0)
        {
            return new WheelContent([], Notice(paths, formats), []);
        }
        var combine = formats.Count > 1 && formats.All(format => format.IsImage());
        var subject = paths.Count == 1 ? Path.GetFileName(paths[0])
            : formats.Count == paths.Count && combine ? $"{paths.Count} images"
            : formats.Count == paths.Count && formats.All(format => format == FileFormat.Pdf) ? $"{paths.Count} PDFs"
            : $"{paths.Count} files";
        var wedgeHubs = options.Select(option =>
                !option.IsEnabled ? new HubText("Not available", option.Label, option.Hint)
                : paths.Count == 1 ? new HubText("Save as", option.Label, TitleOnPill: true)
                : combine && option.Target == FileFormat.Pdf ? new HubText("Combine into", "One PDF", TitleOnPill: true)
                : new HubText("Save each as", option.Label, TitleOnPill: true))
            .ToList();
        return new WheelContent(options, new HubText("Convert", subject), wedgeHubs);
    }

    /// <summary>What the notice says when the dragged files have nothing to become.</summary>
    private static HubText Notice(IReadOnlyList<string> paths, IReadOnlyList<FileFormat> formats)
    {
        if (formats.Count > 0 && formats.Distinct().Count() > 1)
        {
            return new HubText("Can't convert", "These files", "They have no format in common. Drag files of one kind.");
        }

        if (formats.Count > 0)
        {
            var kind = formats[0].Plural();
            return new HubText("Can't convert", char.ToUpperInvariant(kind[0]) + kind[1..], "Converting them isn't available yet.");
        }

        // Not a kind of file Sulap knows: name it by its extension.
        var extensions = paths
            .Select(path => Path.GetExtension(path).TrimStart('.').ToUpperInvariant())
            .Where(extension => extension.Length is > 0 and <= 8)
            .Distinct()
            .ToList();
        var title = extensions.Count == 1
            ? $"{extensions[0]} {(paths.Count == 1 ? "file" : "files")}"
            : paths.Count == 1 ? "This file" : "These files";
        return new HubText("Can't convert", title, $"{AppIdentity.Name} converts PDFs, images, and Office documents.");
    }

    /// <summary>Shows the invisible sensor centered on a point in physical pixels.</summary>
    /// <param name="nudge">Move the pointer by nothing, so OLE notices the new window under a pointer that holds still.</param>
    internal void ShowSensorAt(POINT point, IntPtr insertAfter, bool nudge)
    {
        if (_disposed)
        {
            return;
        }

        _dropWait.Stop();
        _window.View.ShowSensor();
        _window.ShowCenteredAt(point, insertAfter);
        State = WheelState.Sensor;
        _buttonlessTicks = 0;
        _pointerOver = false;
        if (_watchMouseButtons)
        {
            _watchdog.Start();
        }

        if (nudge)
        {
            var input = new[] { new INPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_MOVE } } };
            SendInput(1, input, Marshal.SizeOf<INPUT>());
        }
    }

    /// <summary>The middle of a wedge of the open wheel, in physical screen pixels.</summary>
    internal POINT WedgeScreenPoint(int wedge) => _window.ToScreen(_window.View.WedgeCenter(wedge));

    /// <summary>The hub's center, in physical screen pixels.</summary>
    internal POINT HubScreenPoint() => _window.ToScreen(new Point(_window.View.ActualWidth / 2, _window.View.ActualHeight / 2));

    private static bool IsAnyMouseButtonDown() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

    private static bool AllowsCopy(DragEventArgs e) => e.AllowedEffects.HasFlag(DragDropEffects.Copy);

    private void OnWheelRequested(WheelKind kind, IntPtr dragImage)
    {
        // The Tools and Actions wheels come in later phases; until then Ctrl and Alt change nothing.
        if (kind != WheelKind.Convert || State is WheelState.Sensor or WheelState.Open)
        {
            return;
        }

        // Without the shell's drag image this may not be a file drag at all (a game drag-selecting
        // with Shift, say): never put a window over a full-screen app.
        if (dragImage == IntPtr.Zero && FullScreenAppInFront())
        {
            return;
        }

        GetCursorPos(out var cursor);
        ShowSensorAt(cursor, dragImage, nudge: true);
    }

    private static bool FullScreenAppInFront() =>
        SHQueryUserNotificationState(out var state) == 0
        && state is QUNS_BUSY or QUNS_RUNNING_D3D_FULL_SCREEN or QUNS_PRESENTATION_MODE;

    private void OnDragEnded(DragEndReason reason)
    {
        if (State == WheelState.Open && _window.View.Shown is { IsNotice: false } && reason == DragEndReason.MouseReleased && _pointerOver)
        {
            _dropWait.Start(); // Drop closes the wheel when it comes; otherwise this timer does.
        }
        else if (State == WheelState.Open)
        {
            Close(); // Cancelled, dropped somewhere else, or only a notice: nothing is dropped on it.
        }
        else if (State == WheelState.Sensor)
        {
            HideNow();
        }
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (State == WheelState.Sensor)
        {
            var content = AllowsCopy(e) && FilesOf(e) is { } paths && _engine.Registry is { } registry ? Describe(paths, registry) : null;
            if (content is null)
            {
                // Not files we can convert: step aside so the drop lands where it normally would.
                e.Effects = DragDropEffects.None;
                HideNow();
                return;
            }

            _window.View.ShowWheel(content, WheelTheme.Current(), animate: SystemParameters.ClientAreaAnimation);
            State = WheelState.Open;
        }

        UpdateHover(e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _buttonlessTicks = 0;
        UpdateHover(e);
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _pointerOver = false;
        _window.View.SetHovered(-1);
        if (State == WheelState.Open && _window.View.Shown is { IsNotice: true })
        {
            Close(); // Nothing to go back to: the notice has done its job.
        }
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        UpdateHover(e);
        var wedge = _window.View.Hovered;
        if (e.Effects == DragDropEffects.Copy && wedge >= 0 && _window.View.Shown is { } content && FilesOf(e) is { } paths)
        {
            var request = new ConversionRequest(paths, content.Options[wedge].Target);
            // File Explorer waits for this call to return; start the work after it has.
            _window.Dispatcher.BeginInvoke(() => _jobs.Start(request));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        Close();
    }

    /// <summary>
    /// Copy on an enabled wedge, nothing anywhere else. Never Move: Shift means "move" in File
    /// Explorer, and a target that answers Move can make Explorer delete the original.
    /// </summary>
    private void UpdateHover(DragEventArgs e)
    {
        if (State != WheelState.Open || _window.View.Shown is not { } content)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var hit = _window.View.WheelHitAt(e.GetPosition(_window.View));
        var wedge = hit.Kind == WheelHitKind.Wedge ? hit.Wedge : -1;
        _pointerOver = true;
        _window.View.SetHovered(wedge);
        e.Effects = wedge >= 0 && content.Options[wedge].IsEnabled && AllowsCopy(e) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private static string[]? FilesOf(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop, autoConvert: false)
        && e.Data.GetData(DataFormats.FileDrop, autoConvert: false) is string[] { Length: > 0 } paths
            ? paths
            : null;

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        // A safety net for missed drag-end signals: a window left on the screen (invisible, as the
        // sensor) would swallow clicks.
        if (IsAnyMouseButtonDown())
        {
            _buttonlessTicks = 0;
        }
        else if (++_buttonlessTicks >= 2)
        {
            Close();
        }
    }

    private void Close()
    {
        _dropWait.Stop();
        _watchdog.Stop();
        switch (State)
        {
            case WheelState.Sensor:
                HideNow();
                break;
            case WheelState.Open:
                State = WheelState.Closing;
                _window.View.Close(SystemParameters.ClientAreaAnimation, () =>
                {
                    if (State == WheelState.Closing)
                    {
                        HideNow();
                    }
                });
                break;
        }
    }

    private void HideNow()
    {
        _dropWait.Stop();
        _watchdog.Stop();
        State = WheelState.Hidden;
        if (_window.IsVisible)
        {
            _window.Hide();
        }

        _window.View.ShowSensor();
    }
}
