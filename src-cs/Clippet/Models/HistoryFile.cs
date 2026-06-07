using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clippet.Models;

/// <summary>JSON DTOs for history.json (version 1). Kept separate from the runtime
/// <see cref="ClipItem"/> so the on-disk shape is explicit and stable.</summary>
public sealed class HistoryFileDto
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("items")] public List<ClipItemDto> Items { get; set; } = [];
}

public sealed class ClipItemDto
{
    [JsonPropertyName("id")] public ulong Id { get; set; }

    [JsonPropertyName("type")]
    [JsonConverter(typeof(ItemTypeJsonConverter))]
    public ItemType Kind { get; set; }

    [JsonPropertyName("content_b64")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentB64 { get; set; }

    [JsonPropertyName("preview")] public string Preview { get; set; } = "";
    [JsonPropertyName("ts")] public ulong Ts { get; set; }
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    [JsonPropertyName("lang")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Lang { get; set; }

    [JsonPropertyName("content_hash")] public ulong ContentHash { get; set; }

    [JsonPropertyName("media_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MediaFile { get; set; }

    [JsonPropertyName("thumb_file")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThumbFile { get; set; }

    [JsonPropertyName("media_w")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? MediaW { get; set; }

    [JsonPropertyName("media_h")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? MediaH { get; set; }

    public ClipItem ToModel() => new()
    {
        Id = Id,
        Kind = Kind,
        Raw = string.IsNullOrEmpty(ContentB64) ? [] : Convert.FromBase64String(ContentB64),
        Preview = Preview,
        Timestamp = Ts,
        Pinned = Pinned,
        Lang = Lang,
        ContentHash = ContentHash,
        MediaFile = MediaFile,
        ThumbFile = ThumbFile,
        MediaW = MediaW,
        MediaH = MediaH,
    };

    public static ClipItemDto FromModel(ClipItem m) => new()
    {
        Id = m.Id,
        Kind = m.Kind,
        ContentB64 = m.Raw.Length == 0 ? null : Convert.ToBase64String(m.Raw),
        Preview = m.Preview,
        Ts = m.Timestamp,
        Pinned = m.Pinned,
        Lang = m.Lang,
        ContentHash = m.ContentHash,
        MediaFile = m.MediaFile,
        ThumbFile = m.ThumbFile,
        MediaW = m.MediaW,
        MediaH = m.MediaH,
    };
}

public sealed class SettingsDto
{
    [JsonPropertyName("autostart_prompted")] public bool AutostartPrompted { get; set; }
    [JsonPropertyName("autostart_enabled")] public bool AutostartEnabled { get; set; }

    [JsonPropertyName("popup_w")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PopupW { get; set; }

    [JsonPropertyName("popup_h")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PopupH { get; set; }

    /// <summary>null = follow system, true = force light, false = force dark.</summary>
    [JsonPropertyName("theme_override")] public bool? ThemeOverride { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(HistoryFileDto))]
[JsonSerializable(typeof(SettingsDto))]
public partial class ClippetJsonContext : JsonSerializerContext
{
}
