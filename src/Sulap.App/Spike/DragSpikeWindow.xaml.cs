using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Sulap.App.Platform;
using Sulap.Core;
using Sulap.Core.Settings;

namespace Sulap.App.Spike;

/// <summary>The Phase 1 spike's window: guided scenarios, options, and the live log.</summary>
public partial class DragSpikeWindow : Window
{
    private static readonly (string Title, string Steps)[] Scenarios =
    [
        ("A file from a File Explorer window",
            "Start dragging one file from a File Explorer window. While you drag, press and release Shift: a blue circle appears under the pointer. Let go of the mouse over the circle."),
        ("A file from the desktop",
            "Do the same with a file that sits on the desktop."),
        ("Several files, switching keys",
            "Select three files in File Explorer and drag them. Press Ctrl, then Shift, then Alt, one after another. Let go over the circle."),
        ("Dropping somewhere else",
            "Drag a file and press Shift. Then move away from the circle and drop the file back into the folder it came from."),
        ("Cancelling with Esc",
            "Drag a file, press Shift, and press Esc before you let go."),
        ("Text instead of files",
            "Select some text in Notepad or a browser and drag it. Press Shift: the circle should vanish right away. Drop the text into Notepad: it should still arrive there."),
        ("A file inside a ZIP",
            "Open a .zip file in File Explorer, drag a file out of it, and press Shift. Then press Esc."),
        ("A selection rectangle",
            "On the desktop, press the mouse on an empty spot and drag out a selection rectangle, then press Shift. The circle may appear, since the mouse alone can't tell this apart from a file drag; the log should show that no DragEnter arrived."),
        ("Moving a window",
            "Drag any window by its title bar and press Shift while you drag. As before, the circle may appear, but no DragEnter should arrive."),
        ("Dragging with the right mouse button",
            "Drag a file with the right mouse button, press Shift, and let go over the circle. If a menu appears, press Esc."),
        ("Holding still",
            "Uncheck \"Nudge the mouse\" below. Drag a file, press Shift, then keep the mouse completely still for two seconds. Move it a little and let go over the circle. Check the option again afterwards."),
        ("Without the z-order fix",
            "Uncheck \"Keep the drag image above the sensor\" below. Drag a file and press Shift. Look closely: is the file's thumbnail drawn above or below the blue circle? Let go over the circle, then check the option again."),
    ];

    private readonly SpikeLog _log;
    private readonly DragSpikeSession _session;
    private readonly DispatcherTimer _flushTimer;
    private int _scenario = -1;

    public DragSpikeWindow(TriggerKeys triggerKeys)
    {
        InitializeComponent();
        Title = $"Drag detection log — {AppIdentity.Name}";
        _log = new SpikeLog();
        _session = new DragSpikeSession(_log, triggerKeys);
        _session.Start();
        StatusText.Text = _session.HookStatus;
        _flushTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => FlushLog(), Dispatcher);
        ShowScenario();
        FlushLog();
    }

    internal string LogFilePath => _log.FilePath;

    protected override void OnClosed(EventArgs e)
    {
        _flushTimer.Stop();
        _session.WriteSummary();
        _session.Dispose();
        _log.Dispose();
        base.OnClosed(e);
    }

    private void FlushLog()
    {
        var text = _log.Flush();
        if (text.Length > 0)
        {
            LogBox.AppendText(text);
            LogBox.ScrollToEnd();
        }
    }

    private void ShowScenario()
    {
        BackButton.IsEnabled = _scenario > 0;
        NextButton.IsEnabled = _scenario < Scenarios.Length;
        if (_scenario < 0)
        {
            ScenarioTitle.Text = $"{Scenarios.Length} short scenarios";
            ScenarioSteps.Text = "Click Start, then do what each scenario says. Click Next when you're done with one.";
            NextButton.Content = "Start";
        }
        else if (_scenario < Scenarios.Length)
        {
            var (title, steps) = Scenarios[_scenario];
            ScenarioTitle.Text = $"Scenario {_scenario + 1} of {Scenarios.Length}: {title}";
            ScenarioSteps.Text = steps;
            NextButton.Content = _scenario == Scenarios.Length - 1 ? "Finish" : "Next";
        }
        else
        {
            ScenarioTitle.Text = "All done, thank you";
            ScenarioSteps.Text = "Click Copy log and paste the log into the chat. Please also say whether the file's thumbnail was drawn above the blue circle in scenario 1, and in scenario 12.";
            NextButton.Content = "Finish";
        }
    }

    private void OnNextClick(object sender, RoutedEventArgs e) => GoToScenario(_scenario + 1);

    private void OnBackClick(object sender, RoutedEventArgs e) => GoToScenario(_scenario - 1);

    private void GoToScenario(int scenario)
    {
        _scenario = Math.Clamp(scenario, 0, Scenarios.Length);
        _log.WriteRaw(_scenario < Scenarios.Length
            ? $"=== Scenario {_scenario + 1}/{Scenarios.Length}: {Scenarios[_scenario].Title} ==="
            : "=== All scenarios done ===");
        ShowScenario();
        FlushLog();
    }

    private void OnOptionClick(object sender, RoutedEventArgs e)
    {
        _session.TintSensor = TintOption.IsChecked == true;
        _session.KeepDragImageOnTop = ZOrderOption.IsChecked == true;
        _session.NudgeMouse = NudgeOption.IsChecked == true;
        _log.Write("option", $"blue circle: {OnOff(_session.TintSensor)}, keep drag image on top: {OnOff(_session.KeepDragImageOnTop)}, nudge: {OnOff(_session.NudgeMouse)}");
        FlushLog();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        _session.WriteSummary();
        FlushLog();
        try
        {
            Clipboard.SetText(_log.Text);
            StatusText.Text = "Copied. Paste it into the chat.";
        }
        catch (ExternalException ex)
        {
            MessageBox.Show(this, $"Couldn't copy the log ({ex.Message}). Try again, or use Open log folder.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Shell.ShowInExplorer(_log.FilePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException)
        {
            MessageBox.Show(this, $"Couldn't open File Explorer: {ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string OnOff(bool value) => value ? "on" : "off";
}
