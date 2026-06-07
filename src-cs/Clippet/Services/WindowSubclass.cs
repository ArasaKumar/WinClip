namespace Clippet.Services;

/// <summary>Subclasses a window's HWND so we can observe WM_HOTKEY / WM_CLIPBOARDUPDATE /
/// tray-callback messages that WinUI 3 windows don't surface. Keeps the delegate alive.</summary>
public sealed class WindowSubclass : IDisposable
{
    public delegate bool MessageHandler(uint msg, nuint wParam, nint lParam, out nint result);

    private readonly IntPtr _hwnd;
    private readonly Native.SUBCLASSPROC _proc; // field keeps the delegate from being collected
    private readonly MessageHandler _handler;
    private const nuint SubclassId = 1;
    private bool _disposed;

    public WindowSubclass(IntPtr hwnd, MessageHandler handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _proc = SubclassProc;
        Native.SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    private IntPtr SubclassProc(IntPtr hWnd, uint uMsg, nuint wParam, nint lParam, nuint uId, nuint dwRef)
    {
        if (_handler(uMsg, wParam, lParam, out nint result))
            return result;
        return Native.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }
}
