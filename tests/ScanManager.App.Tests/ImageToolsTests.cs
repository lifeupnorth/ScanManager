using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanManager.App.Imaging;
using ScanManager.Core.Scanning;

namespace ScanManager.App.Tests;

public sealed class ImageToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScanManagerAppTests", Guid.NewGuid().ToString("N"));

    public ImageToolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs <paramref name="func"/> on a brand-new thread, like a thread-pool hop to a different worker.</summary>
    private static T OnNewThread<T>(Func<T> func)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        });
        thread.Start();
        thread.Join();
        if (error is not null) throw new AggregateException(error);
        return result;
    }

    private string WriteTestPng(int width = 120, int height = 80, PixelFormat? format = null)
    {
        var fmt = format ?? PixelFormats.Gray8;
        int stride = (width * fmt.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(i * 7);
        string path = Path.Combine(_dir, $"{Guid.NewGuid():N}.png");
        ImageTools.SavePng(BitmapSource.Create(width, height, 300, 300, fmt, null, pixels, stride), path);
        return path;
    }

    [Fact]
    public void A_page_loaded_on_one_thread_can_be_cropped_on_another()
    {
        string path = WriteTestPng();
        var loaded = OnNewThread(() => ImageTools.LoadFile(path));

        var cropped = OnNewThread(() => ImageTools.Crop(loaded, new Int32Rect(10, 10, 50, 40)));

        Assert.Equal(50, cropped.PixelWidth);
        Assert.Equal(40, cropped.PixelHeight);
        Assert.True(cropped.IsFrozen);
    }

    [Fact]
    public void A_page_loaded_on_one_thread_can_be_rotated_then_cropped_on_others()
    {
        string path = WriteTestPng();
        var loaded = OnNewThread(() => ImageTools.LoadFile(path));
        var rotated = OnNewThread(() => ImageTools.Rotate(loaded, 90));

        var cropped = OnNewThread(() => ImageTools.Crop(rotated, new Int32Rect(0, 0, 40, 60)));

        Assert.Equal((80, 120), (rotated.PixelWidth, rotated.PixelHeight));
        Assert.Equal((40, 60), (cropped.PixelWidth, cropped.PixelHeight));
    }

    [Fact]
    public void Imported_frames_can_be_processed_on_another_thread()
    {
        string path = WriteTestPng(format: PixelFormats.Bgr24);
        var frames = OnNewThread(() => ImageTools.LoadAllFrames(path));

        var normalized = OnNewThread(() => ImageTools.NormalizeImported(frames[0]));
        var thumbnail = OnNewThread(() => ImageTools.CreateThumbnail(normalized));

        Assert.Equal(PixelFormats.Bgr24, normalized.Format);
        Assert.True(thumbnail.PixelWidth <= ImageTools.ThumbnailSize);
    }

    [Fact]
    public void Loaded_pages_keep_their_resolution_and_format()
    {
        string path = WriteTestPng();
        var loaded = ImageTools.LoadFile(path);
        Assert.Equal(300, loaded.DpiX, 0);
        Assert.Equal(PixelFormats.Gray8, loaded.Format);
    }

    [Fact]
    public void Black_and_white_scans_become_one_bit_pages()
    {
        string path = WriteTestPng();
        var scan = new ScannedImage(File.ReadAllBytes(path), 300, 1, ColorMode.Grayscale, false, false);
        var result = OnNewThread(() => ImageTools.ProcessScan(scan,
            new ScanProcessingOptions(ColorMode.BlackAndWhite, 0, 0, 128, DetectBlankPage: false)));

        Assert.Equal(PixelFormats.BlackWhite, result.Image.Format);
        Assert.True(ImageTools.IsBlackWhite(result.Image));
    }
}
