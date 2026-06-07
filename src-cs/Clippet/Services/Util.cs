using System.Text;
using Clippet.Models;

namespace Clippet.Services;

public static class Util
{
    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        ulong h = 0xcbf29ce484222325UL;
        foreach (byte b in data) { h ^= b; h *= 0x100000001b3UL; }
        return h;
    }

    public static ulong NowUnix() => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public static string RelativeTime(ulong ts, ulong? now = null)
    {
        ulong n = now ?? NowUnix();
        long diff = (long)n - (long)ts;
        if (diff < 0) diff = 0;
        if (diff < 5) return "just now";
        if (diff < 60) return $"{diff}s ago";
        if (diff < 3600) return $"{diff / 60}m ago";
        if (diff < 86400) return $"{diff / 3600}h ago";
        return $"{diff / 86400}d ago";
    }

    /// <summary>Collapse whitespace, trim, cut to max chars, append … if cut.</summary>
    public static string TruncatePreview(string s, int max)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(c is '\r' or '\n' or '\t' ? ' ' : c);
        string flat = sb.ToString().Trim();
        // collapse runs of spaces
        flat = string.Join(' ', flat.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length <= max) return flat;
        return flat[..max].TrimEnd() + "…";
    }

    public static string FirstNonemptyLine(string s, int max)
    {
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0) return TruncatePreview(t, max);
        }
        return TruncatePreview(s, max);
    }

    private static readonly string[] IdeMarkers =
    {
        "Visual Studio Code", "JetBrains", "IntelliJ", "PyCharm", "Rider",
        "GoLand", "WebStorm", "Notepad++", "Microsoft Visual Studio",
    };

    /// <summary>Classify plain text as Code (with a language hint) vs Text using the
    /// foreground window title captured at copy time.</summary>
    public static (ItemType kind, string? lang) ClassifyText(string text, string? foregroundTitle)
    {
        if (foregroundTitle is { Length: > 0 } title && IdeMarkers.Any(m => title.Contains(m, StringComparison.Ordinal)))
            return (ItemType.Code, ExtractLang(title));
        return (ItemType.Text, null);
    }

    /// <summary>Last ".ext" in the title (1–6 alphanumeric/underscore chars), lowercased.</summary>
    public static string? ExtractLang(string title)
    {
        string? found = null;
        for (int i = 0; i < title.Length; i++)
        {
            if (title[i] != '.') continue;
            int j = i + 1;
            while (j < title.Length && (char.IsLetterOrDigit(title[j]) || title[j] == '_')) j++;
            int len = j - (i + 1);
            if (len is >= 1 and <= 6)
                found = title.Substring(i + 1, len).ToLowerInvariant();
        }
        return found;
    }

    // ---------------- HTML (CF_HTML) → plain ----------------
    public static string CfHtmlToPlain(byte[] data)
    {
        string all = Encoding.UTF8.GetString(data);
        int start = FindOffset(all, "StartFragment:");
        int end = FindOffset(all, "EndFragment:");
        string html;
        if (start >= 0 && end > start && end <= all.Length)
            html = SliceByByteOffset(data, start, end);
        else
        {
            int lt = all.IndexOf('<');
            html = lt >= 0 ? all[lt..] : all;
        }
        return TruncatePreview(StripTags(html), 200);
    }

    private static int FindOffset(string header, string key)
    {
        int k = header.IndexOf(key, StringComparison.Ordinal);
        if (k < 0) return -1;
        int p = k + key.Length;
        int start = p;
        while (p < header.Length && char.IsDigit(header[p])) p++;
        if (p == start) return -1;
        return int.TryParse(header.AsSpan(start, p - start), out int v) ? v : -1;
    }

    private static string SliceByByteOffset(byte[] data, int startByte, int endByte)
    {
        startByte = Math.Clamp(startByte, 0, data.Length);
        endByte = Math.Clamp(endByte, startByte, data.Length);
        return Encoding.UTF8.GetString(data, startByte, endByte - startByte);
    }

    public static string StripTags(string html)
    {
        var sb = new StringBuilder(html.Length);
        bool inTag = false;
        foreach (char c in html)
        {
            if (c == '<') { inTag = true; continue; }
            if (c == '>') { inTag = false; sb.Append(' '); continue; }
            if (!inTag) sb.Append(c);
        }
        return DecodeEntities(sb.ToString());
    }

    public static string DecodeEntities(string s)
    {
        if (s.IndexOf('&') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '&') { sb.Append(s[i]); continue; }
            int semi = s.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 10) { sb.Append('&'); continue; }
            string ent = s.Substring(i + 1, semi - i - 1);
            string? rep = ent switch
            {
                "amp" => "&",
                "lt" => "<",
                "gt" => ">",
                "quot" => "\"",
                "apos" => "'",
                "nbsp" => " ",
                _ => null,
            };
            if (rep == null && ent.StartsWith('#'))
            {
                bool hex = ent.Length > 1 && (ent[1] == 'x' || ent[1] == 'X');
                string num = hex ? ent[2..] : ent[1..];
                if (int.TryParse(num, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer, null, out int cp) && cp > 0)
                    rep = char.ConvertFromUtf32(cp);
            }
            if (rep != null) { sb.Append(rep); i = semi; }
            else sb.Append('&');
        }
        return sb.ToString();
    }

    // ---------------- RTF → plain ----------------
    private static readonly HashSet<string> RtfSkipDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "listtable",
        "listoverridetable", "rsidtbl", "generator", "themedata", "datastore",
        "latentstyles", "xmlnstbl", "revtbl",
    };

    public static string RtfToPlain(byte[] data)
    {
        string rtf = Encoding.ASCII.GetString(data);
        var sb = new StringBuilder(rtf.Length);
        var skipDepth = new Stack<bool>();   // is this group a skipped destination
        int depth = 0;
        bool skipping = false;
        int ucSkip = 1;       // \ucN — chars to skip after a \uN
        int pendingSkipChars = 0;

        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];
            if (c == '\\')
            {
                // control word / symbol
                if (i + 1 < rtf.Length && !char.IsLetter(rtf[i + 1]))
                {
                    char ctl = rtf[i + 1];
                    if (ctl == '\'' && i + 3 < rtf.Length)
                    {
                        // \'hh hex byte
                        if (!skipping && pendingSkipChars == 0)
                        {
                            if (int.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out int b))
                                sb.Append((char)b);
                        }
                        if (pendingSkipChars > 0) pendingSkipChars--;
                        i += 4;
                        continue;
                    }
                    if (ctl is '\\' or '{' or '}')
                    {
                        if (!skipping && pendingSkipChars == 0) sb.Append(ctl);
                        i += 2;
                        continue;
                    }
                    if (ctl == '*')
                    {
                        // \* — ignorable destination; mark current group skipped
                        skipping = true;
                        i += 2;
                        continue;
                    }
                    if (ctl is '\r' or '\n') { i += 2; continue; }
                    i += 2;
                    continue;
                }

                int j = i + 1;
                while (j < rtf.Length && char.IsLetter(rtf[j])) j++;
                string word = rtf.Substring(i + 1, j - i - 1);
                int num = 0; bool hasNum = false; int sign = 1;
                if (j < rtf.Length && (rtf[j] == '-' || char.IsDigit(rtf[j])))
                {
                    if (rtf[j] == '-') { sign = -1; j++; }
                    int ns = j;
                    while (j < rtf.Length && char.IsDigit(rtf[j])) j++;
                    if (j > ns) { hasNum = true; num = sign * int.Parse(rtf.AsSpan(ns, j - ns)); }
                }
                if (j < rtf.Length && rtf[j] == ' ') j++; // delimiter space consumed

                switch (word)
                {
                    case "par": case "line": case "sect":
                        if (!skipping) sb.Append('\n'); break;
                    case "tab":
                        if (!skipping) sb.Append('\t'); break;
                    case "uc":
                        if (hasNum) ucSkip = Math.Max(0, num); break;
                    case "u":
                        if (hasNum)
                        {
                            if (!skipping) sb.Append((char)(ushort)(num < 0 ? num + 65536 : num));
                            pendingSkipChars = ucSkip;
                        }
                        break;
                    default:
                        if (RtfSkipDestinations.Contains(word)) skipping = true;
                        break;
                }
                i = j;
                continue;
            }

            if (c == '{')
            {
                skipDepth.Push(skipping);
                depth++;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (skipDepth.Count > 0) skipping = skipDepth.Pop();
                else skipping = false;
                if (depth > 0) depth--;
                i++;
                continue;
            }
            if (c is '\r' or '\n') { i++; continue; }

            if (!skipping)
            {
                if (pendingSkipChars > 0) pendingSkipChars--;
                else sb.Append(c);
            }
            i++;
        }

        return TruncatePreview(sb.ToString(), 200);
    }
}
