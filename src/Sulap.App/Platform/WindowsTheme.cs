using Microsoft.Win32;

namespace Sulap.App.Platform;

/// <summary>The light/dark choices from Settings → Personalization → Colors.</summary>
internal static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True when the taskbar, Start, and other system surfaces use the light theme.</summary>
    public static bool TaskbarUsesLightTheme => ReadFlag("SystemUsesLightTheme", fallback: false);

    /// <summary>True when apps should use the light theme.</summary>
    public static bool AppsUseLightTheme => ReadFlag("AppsUseLightTheme", fallback: true);

    private static bool ReadFlag(string name, bool fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
        return key?.GetValue(name) is int value ? value != 0 : fallback;
    }
}
