using System.Security.Principal;
using Sulap.App.Interop;

namespace Sulap.App.Platform;

internal static class ProcessInfo
{
    /// <summary>
    /// True when the app runs elevated ("Run as administrator") while UAC is on. Windows then blocks
    /// drops from File Explorer, which runs unelevated, so the wheel can't work.
    /// </summary>
    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return NativeMethods.GetTokenInformation(
                    identity.Token, NativeMethods.TokenElevationType, out var elevationType, sizeof(int), out _)
                && elevationType == NativeMethods.TokenElevationTypeFull;
        }
    }
}
