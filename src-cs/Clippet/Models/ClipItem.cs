namespace Clippet.Models;

/// <summary>One captured clipboard entry. Runtime model; see HistoryFile.cs for the JSON DTO.</summary>
public sealed class ClipItem
{
    public ulong Id;
    public ItemType Kind;

    /// <summary>Raw payload bytes (UTF-16 text, RTF, CF_HTML, TSV, HDROP paths joined by \0…).
    /// Empty for images — image bytes live on disk as PNG.</summary>
    public byte[] Raw = [];

    public string Preview = "";
    public ulong Timestamp;
    public bool Pinned;

    /// <summary>Language hint for Code items (file extension, lowercase).</summary>
    public string? Lang;

    /// <summary>FNV-1a/64 of the source bytes (PNG bytes for images, Raw otherwise).</summary>
    public ulong ContentHash;

    public string? MediaFile;
    public string? ThumbFile;
    public uint? MediaW;
    public uint? MediaH;

    public string Tag => Kind.Tag(Lang);
    public bool IsImage => Kind == ItemType.Image;
}
