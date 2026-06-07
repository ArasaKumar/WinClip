using Windows.UI;

namespace Clippet.Models;

/// <summary>Light/dark color tables, including the per-format tag colors used by the row pill.</summary>
public sealed class Palette
{
    public Color Bg;
    public Color RowSel;
    public Color Text;
    public Color Subtext;
    public Color Accent;      // pin / theme highlight
    public Color PinDim;
    public Color SearchBg;

    public Color TagText;
    public Color TagRich;
    public Color TagImage;
    public Color TagFile;
    public Color TagHtml;
    public Color TagSheet;
    public Color TagCode;

    public static readonly Color CloseHover = Hex("#C42B1C");

    public Color TagColor(ItemType t) => t switch
    {
        ItemType.Text => TagText,
        ItemType.RichText => TagRich,
        ItemType.Image => TagImage,
        ItemType.File => TagFile,
        ItemType.Html => TagHtml,
        ItemType.Spreadsheet => TagSheet,
        ItemType.Code => TagCode,
        _ => TagText,
    };

    public static readonly Palette Dark = new()
    {
        Bg = Hex("#202020"),
        RowSel = Hex("#3C3C3C"),
        Text = Hex("#F0F0F0"),
        Subtext = Hex("#A0A0A0"),
        Accent = Hex("#FACC15"),
        PinDim = Hex("#606060"),
        SearchBg = Hex("#333333"),
        TagText = Hex("#CBD5E1"),
        TagRich = Hex("#FCA5A5"),
        TagImage = Hex("#86EFAC"),
        TagFile = Hex("#FCD34D"),
        TagHtml = Hex("#C4B5FD"),
        TagSheet = Hex("#5EEAD4"),
        TagCode = Hex("#93C5FD"),
    };

    public static readonly Palette Light = new()
    {
        Bg = Hex("#FAFAFA"),
        RowSel = Hex("#E5E5E5"),
        Text = Hex("#111111"),
        Subtext = Hex("#606060"),
        Accent = Hex("#D97706"),
        PinDim = Hex("#B0B0B0"),
        SearchBg = Hex("#F1F1F1"),
        TagText = Hex("#475569"),
        TagRich = Hex("#DC2626"),
        TagImage = Hex("#16A34A"),
        TagFile = Hex("#CA8A04"),
        TagHtml = Hex("#7C3AED"),
        TagSheet = Hex("#0D9488"),
        TagCode = Hex("#2563EB"),
    };

    public static Palette For(bool light) => light ? Light : Dark;

    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        byte a = hex.Length >= 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)0xFF;
        return Color.FromArgb(a, r, g, b);
    }
}
