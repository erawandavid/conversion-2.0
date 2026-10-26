using System.IO;
using System.Security;
using System.Windows;
using Sulap.App.Interop;
using Sulap.App.Platform;
using Sulap.App.Tray;
using Sulap.App.Views;
using Sulap.Core;
using Sulap.Core.Settings;

namespace Sulap.App;

/// <summary>A tray app: no main window, just the notification-area icon and the windows it opens.</summary>
public partial class App : Application
{
    private readonly SettingsStore _settingsStore = SettingsStore.ForCurrentUser();
    private SingleInstance? _instance;
    private TrayIcon? _tray;
    private bool _paused;

    internal AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);

#if DEBUG
        if (Diagnostics.DebugCommands.TryRun(e.Args, out var exitCode))
        {
            Shutdown(exitCode);
            return;
        }
#endif

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
        _tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private IReadOnlyList<TrayMenuItem> BuildMenu() =>
    [
        new("Settings…", ShowSettings),
        new("About", ShowAbout),
        TrayMenuItem.Separator,
        new("Start with Windows", ToggleAutoStart, IsChecked: AutoStart.IsEnabled),
        new("Pause", TogglePause, IsChecked: _paused),
        TrayMenuItem.Separator,
        new("Quit", () => Shutdown()),
    ];

    private void ShowSettings() => ShowWindow(() => new SettingsWindow(_settingsStore.FilePath));

    private void ShowAbout() => ShowWindow(() => new AboutWindow());

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
