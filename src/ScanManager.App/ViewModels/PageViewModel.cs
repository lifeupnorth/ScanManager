using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanManager.App.Imaging;

namespace ScanManager.App.ViewModels;

/// <summary>One page of the document being assembled. The image itself lives in a file.</summary>
public sealed partial class PageViewModel : ObservableObject
{
    private PageViewModel(PageState state) => Apply(state);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial string FilePath { get; private set; }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PixelSize), nameof(Details))]
    public partial int PixelWidth { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PixelSize), nameof(Details))]
    public partial int PixelHeight { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    public partial double Dpi { get; private set; }

    public string Title => $"Page {Number}";

    public Size PixelSize => new(PixelWidth, PixelHeight);

    public string Details
    {
        get
        {
            double dpi = Dpi > 1 ? Dpi : 96;
            return $"{PixelWidth} × {PixelHeight} px · {Dpi:0} dpi · {PixelWidth / dpi:0.##} × {PixelHeight / dpi:0.##} in";
        }
    }

    /// <summary>Builds a page for an image that has already been written to <paramref name="filePath"/>. Thread-safe.</summary>
    public static PageViewModel Create(string filePath, BitmapSource image) => new(PageState.From(filePath, image));

    public PageState State => new(FilePath, Thumbnail, PixelWidth, PixelHeight, Dpi);

    public void Apply(PageState state)
    {
        FilePath = state.FilePath;
        Thumbnail = state.Thumbnail;
        PixelWidth = state.PixelWidth;
        PixelHeight = state.PixelHeight;
        Dpi = state.Dpi;
    }
}

/// <summary>An immutable snapshot of a page, used for edits and undo.</summary>
public sealed record PageState(string FilePath, ImageSource? Thumbnail, int PixelWidth, int PixelHeight, double Dpi)
{
    public static PageState From(string filePath, BitmapSource image) =>
        new(filePath, ImageTools.CreateThumbnail(image), image.PixelWidth, image.PixelHeight, image.DpiX);
}
