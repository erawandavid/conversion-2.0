using Microsoft.Win32;
using Sulap.Core;

namespace Sulap.App.Platform;

/// <summary>"Start with Windows": a value under the per-user Run key that points to this executable.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Where Task Manager and Settings → Apps → Startup record that the user switched an entry off.
    private const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown.");

    /// <summary>
    /// True when Windows will start this executable at sign-in: the Run value points here and
    /// the entry wasn't switched off in Task Manager.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(AppIdentity.Name) is not string command
                || !string.Equals(command.Trim().Trim('"'), ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            // The first byte is even when the entry is enabled (0x02) and odd when it's switched off (0x03).
            return approved?.GetValue(AppIdentity.Name) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
        }
    }

    public static void Enable()
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run.SetValue(AppIdentity.Name, $"\"{ExecutablePath}\"", RegistryValueKind.String);
        }

        ClearSwitchedOffState();
    }

    public static void Disable()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            run?.DeleteValue(AppIdentity.Name, throwOnMissingValue: false);
        }

        ClearSwitchedOffState();
    }

    private static void ClearSwitchedOffState()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedKey, writable: true);
        approved?.DeleteValue(AppIdentity.Name, throwOnMissingValue: false);
    }
}
