using System.Runtime.InteropServices;
using System.Text;
using Clippet.Models;

namespace Clippet.Services;

/// <summary>Reads the most-informative clipboard format (priority order) promptly on the
/// message thread, then prepares the item (decode/preview/encode) on a background thread.</summary>
public sealed class ClipboardMonitor
{
    private readonly uint _cfRtf;
    private readonly uint _cfHtml;
    private readonly uint _cfSheet;

    public ClipboardMonitor()
    {
        _cfRtf = Native.RegisterClipboardFormatW("Rich Text Format");
        _cfHtml = Native.RegisterClipboardFormatW("HTML Format");
        _cfSheet = Native.RegisterClipboardFormatW("XML Spreadsheet");
    }

    public enum RawKind { File, Spreadsheet, Rtf, Html, Image, UnicodeText, AnsiText }

    public sealed class RawCapture
    {
        public RawKind Kind;
        public byte[] Bytes = [];
        public string? ForegroundTitle;
        public List<string>? Files;
    }

    /// <summary>Quick, message-thread read while the clipboard is owned. Returns null if nothing
    /// usable is present or the clipboard could not be opened.</summary>
    public RawCapture? ReadClipboard(IntPtr hwnd)
    {
        if (!Native.OpenClipboard(hwnd)) return null;
        try
        {
            string? title = GetForegroundTitle();

            if (Native.IsClipboardFormatAvailable(Native.CF_HDROP))
            {
                var files = ReadHdrop();
                if (files is { Count: > 0 })
                    return new RawCapture { Kind = RawKind.File, Files = files, ForegroundTitle = title };
            }

            if (_cfSheet != 0 && Native.IsClipboardFormatAvailable(_cfSheet))
            {
                // Prefer the parallel CF_TEXT (TSV); fall back to the raw XML.
                byte[]? tsv = ReadBytes(Native.CF_TEXT);
                byte[] bytes = tsv ?? ReadBytes(_cfSheet) ?? [];
                if (bytes.Length > 0)
                    return new RawCapture { Kind = RawKind.Spreadsheet, Bytes = bytes, ForegroundTitle = title };
            }

            if (_cfRtf != 0 && Native.IsClipboardFormatAvailable(_cfRtf))
            {
                byte[]? b = ReadBytes(_cfRtf);
                if (b is { Length: > 0 })
                    return new RawCapture { Kind = RawKind.Rtf, Bytes = b, ForegroundTitle = title };
            }

            if (_cfHtml != 0 && Native.IsClipboardFormatAvailable(_cfHtml))
            {
                byte[]? b = ReadBytes(_cfHtml);
                if (b is { Length: > 0 })
                    return new RawCapture { Kind = RawKind.Html, Bytes = b, ForegroundTitle = title };
            }

            if (Native.IsClipboardFormatAvailable(Native.CF_DIB))
            {
                byte[]? b = ReadBytes(Native.CF_DIB);
                if (b is { Length: > 0 })
                    return new RawCapture { Kind = RawKind.Image, Bytes = b, ForegroundTitle = title };
            }

            if (Native.IsClipboardFormatAvailable(Native.CF_UNICODETEXT))
            {
                byte[]? b = ReadBytes(Native.CF_UNICODETEXT);
                if (b is { Length: > 0 })
                    return new RawCapture { Kind = RawKind.UnicodeText, Bytes = b, ForegroundTitle = title };
            }

            if (Native.IsClipboardFormatAvailable(Native.CF_TEXT))
            {
                byte[]? b = ReadBytes(Native.CF_TEXT);
                if (b is { Length: > 0 })
                    return new RawCapture { Kind = RawKind.AnsiText, Bytes = b, ForegroundTitle = title };
            }

            return null;
        }
        finally
        {
            Native.CloseClipboard();
        }
    }

    /// <summary>Decode/preview/encode off the message thread. For images, produces PNG + thumbnail.</summary>
    public async Task<PreparedCapture?> PrepareAsync(RawCapture raw)
    {
        switch (raw.Kind)
        {
            case RawKind.File:
            {
                var files = raw.Files ?? [];
                string preview = files.Count == 0 ? "(no files)"
                    : files.Count == 1 ? Path.GetFileName(files[0])
                    : $"{Path.GetFileName(files[0])} + {files.Count - 1} more";
                byte[] payload = Encoding.UTF8.GetBytes(string.Join('\n', files));
                return new PreparedCapture
                {
                    Kind = ItemType.File,
                    Raw = payload,
                    Preview = Util.TruncatePreview(preview, 80),
                    ContentHash = Util.Fnv1a64(payload),
                };
            }
            case RawKind.Spreadsheet:
            {
                string tsv = DecodeAnsiOrUtf8(raw.Bytes);
                var lines = tsv.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                int rows = lines.Length;
                int cols = 0;
                foreach (var l in lines) cols = Math.Max(cols, l.Count(c => c == '\t') + 1);
                byte[] payload = Encoding.UTF8.GetBytes(tsv);
                return new PreparedCapture
                {
                    Kind = ItemType.Spreadsheet,
                    Raw = payload,
                    Preview = $"{rows} rows x {cols} cols",
                    ContentHash = Util.Fnv1a64(payload),
                };
            }
            case RawKind.Rtf:
                return new PreparedCapture
                {
                    Kind = ItemType.RichText,
                    Raw = raw.Bytes,
                    Preview = Util.TruncatePreview(Util.RtfToPlain(raw.Bytes), 80),
                    ContentHash = Util.Fnv1a64(raw.Bytes),
                };
            case RawKind.Html:
                return new PreparedCapture
                {
                    Kind = ItemType.Html,
                    Raw = raw.Bytes,
                    Preview = Util.TruncatePreview(Util.CfHtmlToPlain(raw.Bytes), 80),
                    ContentHash = Util.Fnv1a64(raw.Bytes),
                };
            case RawKind.Image:
            {
                var decoded = await ImageCodec.DibToPngAsync(raw.Bytes);
                if (decoded == null) return null;
                byte[] thumb = await ImageCodec.MakeThumbnailAsync(decoded.Png, Storage.ThumbMaxEdge);
                return new PreparedCapture
                {
                    Kind = ItemType.Image,
                    Raw = [],
                    Preview = $"[Image {decoded.Width}x{decoded.Height}]",
                    ContentHash = Util.Fnv1a64(decoded.Png),
                    Png = decoded.Png,
                    Thumb = thumb,
                    W = decoded.Width,
                    H = decoded.Height,
                };
            }
            case RawKind.UnicodeText:
            case RawKind.AnsiText:
            {
                string text = raw.Kind == RawKind.UnicodeText
                    ? Encoding.Unicode.GetString(raw.Bytes).TrimEnd('\0')
                    : DecodeAnsiOrUtf8(raw.Bytes).TrimEnd('\0');
                var (kind, lang) = Util.ClassifyText(text, raw.ForegroundTitle);
                string preview = kind == ItemType.Code
                    ? Util.FirstNonemptyLine(text, 80)
                    : Util.TruncatePreview(text, 80);
                byte[] payload = Encoding.UTF8.GetBytes(text);
                return new PreparedCapture
                {
                    Kind = kind,
                    Raw = payload,
                    Preview = preview,
                    Lang = lang,
                    ContentHash = Util.Fnv1a64(payload),
                };
            }
            default:
                return null;
        }
    }

    // ---------------- low-level readers ----------------
    private static byte[]? ReadBytes(uint format)
    {
        IntPtr h = Native.GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        IntPtr ptr = Native.GlobalLock(h);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            int size = (int)Native.GlobalSize(h);
            if (size <= 0) return null;
            byte[] buf = new byte[size];
            Marshal.Copy(ptr, buf, 0, size);
            return buf;
        }
        finally { Native.GlobalUnlock(h); }
    }

    private static List<string>? ReadHdrop()
    {
        IntPtr hDrop = Native.GetClipboardData(Native.CF_HDROP);
        if (hDrop == IntPtr.Zero) return null;
        uint count = Native.DragQueryFileW(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0) return null;
        var list = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint len = Native.DragQueryFileW(hDrop, i, null, 0);
            if (len == 0) continue;
            var buf = new char[len + 1];
            uint got = Native.DragQueryFileW(hDrop, i, buf, len + 1);
            if (got > 0) list.Add(new string(buf, 0, (int)got));
        }
        return list;
    }

    private static string DecodeAnsiOrUtf8(byte[] bytes)
    {
        // CF_TEXT is ANSI; default code page. UTF-8 fallback for safety.
        try { return Encoding.GetEncoding(0).GetString(bytes); }
        catch { return Encoding.UTF8.GetString(bytes); }
    }

    private static string? GetForegroundTitle()
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var buf = new char[512];
        int n = Native.GetWindowTextW(fg, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : null;
    }
}

/// <summary>Result of preparing a capture: ready to dedup-check and add to history.</summary>
public sealed class PreparedCapture
{
    public ItemType Kind;
    public byte[] Raw = [];
    public string Preview = "";
    public string? Lang;
    public ulong ContentHash;
    public byte[]? Png;
    public byte[]? Thumb;
    public uint? W;
    public uint? H;
}
