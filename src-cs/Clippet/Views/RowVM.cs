using System.ComponentModel;
using Clippet.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Clippet.Views;

/// <summary>View-model for one visible list row. Thumbnails load lazily and notify.</summary>
public sealed class RowVM : INotifyPropertyChanged
{
    public required ClipItem Item { get; init; }
    public required int HistIndex { get; init; }
    public required List<int> Indices { get; init; }

    public string Preview => Item.Preview;
    public string TagText => Item.Tag;
    public bool IsImage => Item.IsImage;
    public Visibility ThumbVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;
    public double RowHeight => IsImage ? 102 : 34;

    public required SolidColorBrush TagBrush { get; init; }
    public required SolidColorBrush TextBrush { get; init; }
    public required SolidColorBrush SubtextBrush { get; init; }
    public required string TimeText { get; init; }

    public string PinGlyph => Item.Pinned ? "★" : "☆"; // ★ / ☆
    public required SolidColorBrush PinBrush { get; init; }

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set { _thumb = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
