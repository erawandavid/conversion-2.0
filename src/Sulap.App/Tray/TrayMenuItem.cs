namespace Sulap.App.Tray;

/// <summary>One entry of the tray menu. The menu is rebuilt every time it opens, so its state is always current.</summary>
internal sealed record TrayMenuItem(string Text, Action Invoke, bool IsChecked = false, bool IsEnabled = true)
{
    public static TrayMenuItem Separator { get; } = new("-", () => { });

    public bool IsSeparator => ReferenceEquals(this, Separator);
}
