using System.Runtime.InteropServices;
using System.Text;
using Clippet.Models;

namespace Clippet.Services;

/// <summary>Re-publishes a history item to the clipboard (per-format) and synthesizes Ctrl+V
/// into the previously focused window.</summary>
public sealed class ClipboardWriter
{
    private readonly uint _cfRtf;
    private readonly uint _cfHtml;
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47 };

    public ClipboardWriter()
    {
        _cfRtf = Native.RegisterClipboardFormatW("Rich Text Format");
        _cfHtml = Native.RegisterClipboardFormatW("HTML Format");
    }

    /// <summary>Build the payload (async for images) then publish synchronously. Arms the gate
    /// first so the resulting WM_CLIPBOARDUPDATE is ignored.</summary>
    public async Task<bool> PublishAsync(ClipItem item, ClipboardGate gate, IntPtr hwnd, Storage storage)
    {
        var payload = await BuildPayloadAsync(item, storage);
        if (payload == null) return false;
        var (format, data) = payload.Value;

        gate.Arm();
        if (!Native.OpenClipboard(hwnd)) return false;
        try
        {
            Native.EmptyClipboard();
            IntPtr hMem = AllocAndCopy(data);
            if (hMem == IntPtr.Zero) return false;
            if (Native.SetClipboardData(format, hMem) == IntPtr.Zero)
            {
                Native.GlobalFree(hMem);
                return false;
            }
            return true;
        }
        finally { Native.CloseClipboard(); }
    }

    private async Task<(uint format, byte[] data)?> BuildPayloadAsync(ClipItem item, Storage storage)
    {
        switch (item.Kind)
        {
            case ItemType.Text:
            case ItemType.Code:
            {
                string text = Encoding.UTF8.GetString(item.Raw);
                return (Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
            }
            case ItemType.RichText:
                return (_cfRtf, AppendNull(item.Raw));
            case ItemType.Html:
                return (_cfHtml, AppendNull(item.Raw));
            case ItemType.Spreadsheet:
            {
                string tsv = Encoding.UTF8.GetString(item.Raw);
                byte[] ansi = Encoding.GetEncoding(0).GetBytes(tsv + "\0");
                return (Native.CF_TEXT, ansi);
            }
            case ItemType.File:
            {
                string joined = Encoding.UTF8.GetString(item.Raw);
                var files = joined.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                return (Native.CF_HDROP, BuildHdrop(files));
            }
            case ItemType.Image:
            {
                string path = storage.MediaPath(item.Id);
                if (!File.Exists(path)) return null;
                byte[] bytes = await File.ReadAllBytesAsync(path);
                byte[] dib = IsPng(bytes) ? await ImageCodec.PngToDibAsync(bytes) : bytes;
                return (Native.CF_DIB, dib);
            }
            default:
                return null;
        }
    }

    /// <summary>Hide-then-paste: restore focus to prevFg and synthesize Ctrl+V.</summary>
    public static void PasteInto(IntPtr prevFg)
    {
        if (prevFg != IntPtr.Zero) Native.SetForegroundWindow(prevFg);
        SendCtrlV();
    }

    private static void SendCtrlV()
    {
        var inputs = new Native.INPUT[4];
        inputs[0] = KeyInput((ushort)Native.VK_CONTROL, false);
        inputs[1] = KeyInput((ushort)Native.VK_V, false);
        inputs[2] = KeyInput((ushort)Native.VK_V, true);
        inputs[3] = KeyInput((ushort)Native.VK_CONTROL, true);
        Native.SendInput((uint)inputs.Length, inputs, Native.INPUT.Size);
    }

    private static Native.INPUT KeyInput(ushort vk, bool up) => new()
    {
        type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion
        {
            ki = new Native.KEYBDINPUT
            {
                wVk = vk,
                dwFlags = up ? Native.KEYEVENTF_KEYUP : 0,
            }
        }
    };

    private static IntPtr AllocAndCopy(byte[] data)
    {
        IntPtr h = Native.GlobalAlloc(Native.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        IntPtr ptr = Native.GlobalLock(h);
        if (ptr == IntPtr.Zero) { Native.GlobalFree(h); return IntPtr.Zero; }
        try { Marshal.Copy(data, 0, ptr, data.Length); }
        finally { Native.GlobalUnlock(h); }
        return h;
    }

    private static byte[] AppendNull(byte[] src)
    {
        if (src.Length > 0 && src[^1] == 0) return src;
        byte[] dst = new byte[src.Length + 1];
        System.Buffer.BlockCopy(src, 0, dst, 0, src.Length);
        return dst;
    }

    private static byte[] BuildHdrop(IReadOnlyList<string> files)
    {
        // DROPFILES (20 bytes) + double-null-terminated UTF-16 path list, fWide=1.
        var df = new Native.DROPFILES { pFiles = 20, fWide = 1 };
        var list = new StringBuilder();
        foreach (var f in files) { list.Append(f); list.Append('\0'); }
        list.Append('\0'); // extra terminator
        byte[] pathBytes = Encoding.Unicode.GetBytes(list.ToString());

        byte[] data = new byte[20 + pathBytes.Length];
        IntPtr tmp = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.StructureToPtr(df, tmp, false);
            Marshal.Copy(tmp, data, 0, 20);
        }
        finally { Marshal.FreeHGlobal(tmp); }
        System.Buffer.BlockCopy(pathBytes, 0, data, 20, pathBytes.Length);
        return data;
    }

    private static bool IsPng(byte[] b) =>
        b.Length >= 4 && b[0] == PngSignature[0] && b[1] == PngSignature[1] && b[2] == PngSignature[2] && b[3] == PngSignature[3];
}
