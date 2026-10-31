using System.IO;
using System.Security;
using System.Windows;
using Sulap.App.Interop;
using Sulap.App.Jobs;
using Sulap.App.Platform;
using Sulap.App.Tray;
using Sulap.App.Views;
using Sulap.App.Wheel;
using Sulap.Core;
using Sulap.Core.Settings;

namespace Sulap.App;

/// <summary>A tray app: no main window, just the notification-area icon, the wheel, and the windows they open.</summary>
public partial class App : Application
{
    private readonly SettingsStore _settingsStore = SettingsStore.ForCurrentUser();
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private ConversionJobs? _jobs;
    private WheelController? _wheel;
    private bool _paused;

    internal AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        if (Diagnostics.DebugCommands.IsDebugCommand(e.Args))
        {
            CrashLog.Install(this, interactive: false);
            Shutdown(Diagnostics.DebugCommands.Run(e.Args));
            return;
        }
#endif

        CrashLog.Install(this);
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            SingleInstance.ActivateRunningInstance();
            Shutdown();
            return;
        }

        _instance.ActivationRequested += () => Dispatcher.BeginInvoke(ShowSettings);
        Settings = _settingsStore.LoadOrCreate();
        DarkMode.FollowAppsTheme();
        _tray = new TrayIcon(BuildMenu);

        var engine = new ConversionEngine();
        _jobs = new ConversionJobs(engine);
        _wheel = new WheelController(Settings.Triggers, engine, _jobs);
        UpdateDragDetection();

        if (ProcessInfo.IsElevated)
        {
            MessageBox.Show(
                $"{AppIdentity.Name} is running as administrator. Windows doesn't let File Explorer drop files "
                + "onto apps that run as administrator, so the wheel won't open.\n\n"
                + $"Quit {AppIdentity.Name} and start it again without \"Run as administrator\".",
                AppIdentity.Name,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _wheel?.Dispose();
        // Running conversions get a moment to remove their temporary files and close the Word they started.
        _jobs?.CancelAll(TimeSpan.FromSeconds(5));
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new("Settings…", ShowSettings),
        new("About", ShowAbout),
        new("Drag detection log…", ShowDragSpike),
        TrayMenuItem.Separator,
        new("Start with Windows", ToggleAutoStart, IsChecked: AutoStart.IsEnabled),
        new("Pause", TogglePause, IsChecked: _paused),
        TrayMenuItem.Separator,
        new("Quit", () => Shutdown()),
    ];

    private void ShowSettings() => ShowWindow(() => new SettingsWindow(_settingsStore.FilePath));

    private void ShowAbout() => ShowWindow(() => new AboutWindow());

    /// <summary>Phase 1 spike: logs which drag signals work on this machine.</summary>
    private void ShowDragSpike()
    {
        // The log window runs its own hooks and sensor; the wheel waits until it's closed.
        _wheel?.Stop();
        ShowWindow(() =>
        {
            var window = new Spike.DragSpikeWindow(Settings.Triggers);
            window.Closed += (_, _) => Dispatcher.BeginInvoke(UpdateDragDetection);
            return window;
        });
    }

    /// <summary>The wheel listens for drags unless it's paused or the drag detection log is open.</summary>
    private void UpdateDragDetection()
    {
        if (_paused || Windows.OfType<Spike.DragSpikeWindow>().Any())
        {
            _wheel?.Stop();
        }
        else
        {
            _wheel?.Start();
        }
    }

    /// <summary>Shows the open window of this type, or a new one, in front.</summary>
    private void ShowWindow<T>(Func<T> create)
        where T : Window
    {
        var window = Windows.OfType<T>().FirstOrDefault() ?? create();
        window.Show();
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void TogglePause()
    {
        _paused = !_paused;
        UpdateDragDetection();
        _tray?.Update(_paused ? $"{AppIdentity.Name} (paused)" : AppIdentity.Name, dimmed: _paused);
    }

    private void ToggleAutoStart()
    {
        try
        {
            if (AutoStart.IsEnabled)
            {
                AutoStart.Disable();
            }
            else
            {
                AutoStart.Enable();
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            MessageBox.Show(
                $"Couldn't change \"Start with Windows\": {ex.Message}", AppIdentity.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
