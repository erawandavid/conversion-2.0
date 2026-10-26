using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Sulap.App.Interop;
using Sulap.Core;
using static Sulap.App.Interop.NativeMethods;

namespace Sulap.App.Tray;

/// <summary>
/// The notification-area icon and its menu, through Shell_NotifyIcon. It owns a hidden top-level
/// window to receive callbacks; a message-only window wouldn't get the TaskbarCreated broadcast
/// that Explorer sends when it restarts.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const int CallbackMessage = WM_APP + 1;

    private static readonly int TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

    private readonly Func<IReadOnlyList<TrayMenuItem>> _buildMenu;
    private readonly HwndSource _window;
    private IntPtr _icon;
    private string _toolTip = AppIdentity.Name;
    private bool _dimmed;
    private bool _menuOpen;

    public TrayIcon(Func<IReadOnlyList<TrayMenuItem>> buildMenu)
    {
        _buildMenu = buildMenu;
        _window = new HwndSource(new HwndSourceParameters($"{AppIdentity.Name} tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _window.AddHook(WndProc);
        DarkMode.AllowForWindow(_window.Handle);
        _icon = TrayIconRenderer.CreateIcon(_dimmed);
        Add();
    }

    /// <summary>Sets the tooltip, and whether the icon looks dimmed (paused).</summary>
    public void Update(string toolTip, bool dimmed)
    {
        _toolTip = toolTip;
        if (dimmed != _dimmed)
        {
            _dimmed = dimmed;
            ReplaceIcon();
        }

        var data = CreateData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        var data = CreateData(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        _window.RemoveHook(WndProc);
        _window.Dispose();
        DestroyIcon(_icon);
    }

    private void Add()
    {
        var data = CreateData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        // This fails while the taskbar doesn't exist yet (early in sign-in); TaskbarCreated retries it.
        if (Shell_NotifyIcon(NIM_ADD, ref data))
        {
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    private void ReplaceIcon()
    {
        var oldIcon = _icon;
        _icon = TrayIconRenderer.CreateIcon(_dimmed);
        var data = CreateData(NIF_ICON);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
        DestroyIcon(oldIcon);
    }

    private NOTIFYICONDATAW CreateData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = _toolTip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            // With NOTIFYICON_VERSION_4 the event is in the low word of lParam and the anchor point in wParam.
            switch (LowWord(lParam))
            {
                case WM_CONTEXTMENU:
                case NIN_SELECT:
                case NIN_KEYSELECT:
                    ShowMenu((short)LowWord(wParam), (short)HighWord(wParam));
                    handled = true;
                    break;
            }
        }
        else if (msg == TaskbarCreatedMessage)
        {
            // Explorer restarted, possibly at another DPI: register again with a freshly drawn icon.
            ReplaceIcon();
            Add();
        }
        else if (msg is WM_SETTINGCHANGE or WM_DISPLAYCHANGE)
        {
            // The taskbar theme or the display scale may have changed.
            DarkMode.Refresh();
            ReplaceIcon();
        }

        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        // Enter on a focused icon can arrive twice; ignore it while the menu is up.
        if (_menuOpen)
        {
            return;
        }

        var items = _buildMenu();
        var menu = CreatePopupMenu();
        _menuOpen = true;
        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item.IsSeparator)
                {
                    AppendMenu(menu, MF_SEPARATOR, 0, null);
                    continue;
                }

                var flags = MF_STRING | (item.IsChecked ? MF_CHECKED : 0) | (item.IsEnabled ? 0 : MF_GRAYED);
                AppendMenu(menu, flags, (nuint)(i + 1), item.Text);
            }

            // Without this the menu wouldn't close when the user clicks somewhere else.
            SetForegroundWindow(_window.Handle);
            var alignment = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            var command = TrackPopupMenuEx(
                menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | alignment, x, y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (command > 0)
            {
                Dispatcher.CurrentDispatcher.BeginInvoke(items[command - 1].Invoke);
            }
            else
            {
                // Dismissed (for example with Esc): give keyboard focus back to the notification area.
                var data = CreateData(0);
                Shell_NotifyIcon(NIM_SETFOCUS, ref data);
            }
        }
        finally
        {
            DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    private static int LowWord(IntPtr value) => unchecked((int)(long)value) & 0xFFFF;

    private static int HighWord(IntPtr value) => (unchecked((int)(long)value) >> 16) & 0xFFFF;
}
