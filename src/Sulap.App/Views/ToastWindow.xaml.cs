using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Sulap.Core;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Views;

internal enum ToastKind
{
    Progress,
    Success,
    Warning,
    Error,
    Info,
}

/// <param name="Dismisses">Closes the toast after running, like a notification's button.</param>
internal sealed record ToastAction(string Label, Action Invoke, bool IsPrimary = false, bool Dismisses = false);

/// <param name="CloseAfter">Closes the toast by itself after this long; null keeps it until it's closed.</param>
internal sealed record ToastContent(
    ToastKind Kind, string Title, string? Message = null, IReadOnlyList<ToastAction>? Actions = null, TimeSpan? CloseAfter = null);

/// <summary>
/// A small notice in the corner of the screen that never takes the focus: a conversion's progress,
/// then its result. It closes by itself after a while, but not while the pointer is on it.
/// </summary>
public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;
    private TimeSpan? _closeAfter;
    private bool _hasResult;
    private bool _closing;

    public ToastWindow()
    {
        InitializeComponent();
        Title = AppIdentity.Name;
        _closeTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher);
        _closeTimer.Tick += (_, _) => FadeOutAndClose();
        MouseEnter += (_, _) => _closeTimer.Stop();
        MouseLeave += (_, _) => RestartCloseTimer();
        ContentRendered += (_, _) => FadeIn();
    }

    internal event Action? CancelRequested;

    /// <summary>A new toast, shown in the corner below the others.</summary>
    internal static ToastWindow Open()
    {
        var toast = new ToastWindow();
        ToastStack.Add(toast);
        toast.Show();
        return toast;
    }

    internal void ShowProgress(string title, string status)
    {
        _hasResult = false;
        _closeAfter = null;
        _closeTimer.Stop();
        SetKind(ToastKind.Progress);
        TitleText.Text = title;
        SetMessage(status);
        ProgressIndicator.Visibility = Visibility.Visible;
        ProgressIndicator.IsIndeterminate = true;
        CloseButton.Visibility = Visibility.Collapsed;
        SetActions([new ToastAction("Cancel", () => CancelRequested?.Invoke())]);
    }

    internal void UpdateProgress(string status, double? fraction)
    {
        if (_hasResult)
        {
            return; // A late report must not cover the result.
        }

        SetMessage(status);
        if (fraction is > 0 and < 1)
        {
            ProgressIndicator.IsIndeterminate = false;
            ProgressIndicator.Value = fraction.Value * 100;
        }
    }

    internal void ShowResult(ToastContent content)
    {
        if (_closing)
        {
            return;
        }

        _hasResult = true;
        SetKind(content.Kind);
        TitleText.Text = content.Title;
        SetMessage(content.Message);
        ProgressIndicator.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        SetActions(content.Actions ?? []);
        _closeAfter = content.CloseAfter;
        if (!IsMouseOver)
        {
            RestartCloseTimer();
        }
    }

    internal void FadeOutAndClose()
    {
        _closeTimer.Stop();
        if (_closing)
        {
            return;
        }

        _closing = true;
        if (!SystemParameters.ClientAreaAnimation)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Never take the focus from what the user is doing, and stay out of Alt+Tab.
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    private void FadeIn()
    {
        if (_closing)
        {
            return;
        }

        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
    }

    private void RestartCloseTimer()
    {
        _closeTimer.Stop();
        if (_hasResult && _closeAfter is { } after && !_closing)
        {
            _closeTimer.Interval = after;
            _closeTimer.Start();
        }
    }

    private void SetKind(ToastKind kind)
    {
        // Segoe Fluent Icons: Sync, CompletedSolid, Warning, StatusErrorFull, Info.
        var (glyph, brush) = kind switch
        {
            ToastKind.Progress => ("\uE895", "AccentTextFillColorPrimaryBrush"),
            ToastKind.Success => ("\uEC61", "SystemFillColorSuccessBrush"),
            ToastKind.Warning => ("\uE7BA", "SystemFillColorCautionBrush"),
            ToastKind.Error => ("\uEB90", "SystemFillColorCriticalBrush"),
            _ => ("\uE946", "AccentTextFillColorPrimaryBrush"),
        };
        KindIcon.Text = glyph;
        KindIcon.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    private void SetMessage(string? message)
    {
        MessageText.Text = message ?? string.Empty;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetActions(IReadOnlyList<ToastAction> actions)
    {
        ActionsPanel.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, MinWidth = 96, Margin = new Thickness(0, 0, 8, 0) };
            if (action.IsPrimary)
            {
                button.SetResourceReference(StyleProperty, "AccentButtonStyle");
            }

            button.Click += (_, _) => Run(action);
            ActionsPanel.Children.Add(button);
        }

        ActionsPanel.Visibility = actions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Run(ToastAction action)
    {
        try
        {
            action.Invoke();
        }
        catch (Exception ex)
        {
            AppLog.Write($"The toast button \"{action.Label}\" failed: {ex}");
        }

        if (action.Dismisses)
        {
            FadeOutAndClose();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => FadeOutAndClose();
}

/// <summary>Keeps the open toasts stacked in the primary screen's bottom-right corner, newest at the bottom.</summary>
internal static class ToastStack
{
    // Each toast window has a 16 px transparent margin around its card, for the shadow.
    private const double ShadowMargin = 16;
    private const double Spacing = 8;

    private static readonly List<ToastWindow> Toasts = [];

    public static void Add(ToastWindow toast)
    {
        Toasts.Add(toast);
        toast.SizeChanged += (_, _) => Arrange();
        toast.Closed += (_, _) =>
        {
            Toasts.Remove(toast);
            Arrange();
        };
        Arrange();
    }

    private static void Arrange()
    {
        var area = SystemParameters.WorkArea;
        var bottom = area.Bottom;
        for (var i = Toasts.Count - 1; i >= 0; i--)
        {
            var toast = Toasts[i];
            var width = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
            var height = toast.ActualHeight > 0 ? toast.ActualHeight : 140;
            toast.Left = area.Right - width;
            toast.Top = bottom - height;
            bottom = toast.Top + (2 * ShadowMargin) - Spacing;
        }
    }
}
