using System.Runtime.InteropServices;

namespace Clippet.Services;

/// <summary>System-tray icon + context menu via Shell_NotifyIcon and TrackPopupMenu.
/// The icon's callback message is routed to the owning window's subclass proc.</summary>
public sealed class TrayService : IDisposable
{
    public enum MenuCommand { None = 0, Open = 1, Clear = 2, Settings = 3, Exit = 5 }

    private Native.NOTIFYICONDATAW _nid;
    private readonly IntPtr _hwnd;
    private bool _added;

    public TrayService(IntPtr hwnd, IntPtr hIcon, string tooltip)
    {
        _hwnd = hwnd;
        _nid = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<Native.NOTIFYICONDATAW>(),
            hWnd = hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP,
            uCallbackMessage = Native.WM_TRAYCALLBACK,
            hIcon = hIcon,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
            uVersionOrTimeout = Native.NOTIFYICON_VERSION_4,
        };
        if (Native.Shell_NotifyIconW(Native.NIM_ADD, ref _nid))
        {
            _added = true;
            Native.Shell_NotifyIconW(Native.NIM_SETVERSION, ref _nid);
        }
    }

    public void SetTooltip(string tooltip)
    {
        _nid.szTip = tooltip;
        _nid.uFlags = Native.NIF_TIP | Native.NIF_SHOWTIP | Native.NIF_MESSAGE | Native.NIF_ICON;
        Native.Shell_NotifyIconW(Native.NIM_MODIFY, ref _nid);
    }

    /// <summary>Build and track the right-click menu at the cursor; returns the chosen command.</summary>
    public MenuCommand ShowContextMenu()
    {
        if (!Native.GetCursorPos(out var pt)) return MenuCommand.None;
        IntPtr menu = Native.CreatePopupMenu();
        if (menu == IntPtr.Zero) return MenuCommand.None;
        try
        {
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)MenuCommand.Open, "Open Clippet");
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)MenuCommand.Clear, "Clear History");
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)MenuCommand.Settings, "Settings…");
            Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            Native.AppendMenuW(menu, Native.MF_STRING, (nuint)MenuCommand.Exit, "Exit");

            // Required so the menu dismisses correctly when clicking elsewhere.
            Native.SetForegroundWindow(_hwnd);
            uint cmd = Native.TrackPopupMenu(
                menu,
                Native.TPM_RIGHTBUTTON | Native.TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            Native.PostMessage(_hwnd, 0x0000 /* WM_NULL */, 0, 0);
            return Enum.IsDefined(typeof(MenuCommand), (int)cmd) ? (MenuCommand)cmd : MenuCommand.None;
        }
        finally { Native.DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (!_added) return;
        _added = false;
        Native.Shell_NotifyIconW(Native.NIM_DELETE, ref _nid);
    }
}
