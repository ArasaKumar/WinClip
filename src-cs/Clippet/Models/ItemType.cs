using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clippet.Models;

public enum ItemType
{
    Text,
    RichText,
    Image,
    File,
    Html,
    Spreadsheet,
    Code,
}

public static class ItemTypeExtensions
{
    /// <summary>The short tag shown in the row pill, e.g. [T], [I], [C:rs].</summary>
    public static string Tag(this ItemType t, string? lang = null) => t switch
    {
        ItemType.Text => "[T]",
        ItemType.RichText => "[R]",
        ItemType.Image => "[I]",
        ItemType.File => "[F]",
        ItemType.Html => "[H]",
        ItemType.Spreadsheet => "[X]",
        ItemType.Code => string.IsNullOrEmpty(lang) ? "[C]" : $"[C:{lang}]",
        _ => "[?]",
    };

    public static string ToWire(this ItemType t) => t switch
    {
        ItemType.Text => "text",
        ItemType.RichText => "richtext",
        ItemType.Image => "image",
        ItemType.File => "file",
        ItemType.Html => "html",
        ItemType.Spreadsheet => "spreadsheet",
        ItemType.Code => "code",
        _ => "text",
    };

    public static ItemType FromWire(string? s) => s switch
    {
        "text" => ItemType.Text,
        "richtext" => ItemType.RichText,
        "image" => ItemType.Image,
        "file" => ItemType.File,
        "html" => ItemType.Html,
        "spreadsheet" => ItemType.Spreadsheet,
        "code" => ItemType.Code,
        _ => ItemType.Text,
    };
}

/// <summary>Serializes <see cref="ItemType"/> as the lowercase wire strings.</summary>
public sealed class ItemTypeJsonConverter : JsonConverter<ItemType>
{
    public override ItemType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ItemTypeExtensions.FromWire(reader.GetString());

    public override void Write(Utf8JsonWriter writer, ItemType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWire());
}
