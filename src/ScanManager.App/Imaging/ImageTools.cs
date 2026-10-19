using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanManager.Core.Imaging;
using ScanManager.Core.Scanning;

namespace ScanManager.App.Imaging;

/// <summary>How a freshly scanned image should be finished in software.</summary>
public sealed record ScanProcessingOptions(
    ColorMode ColorMode,
    int Brightness,
    int Contrast,
    byte BlackWhiteThreshold,
    bool DetectBlankPage);

public sealed record ProcessedScan(BitmapSource Image, bool IsBlank);

/// <summary>
/// WPF imaging helpers. Every bitmap returned is frozen and holds its own pixels, so it
/// can be used from any thread: the app decodes and edits pages on thread-pool threads
/// and shows them on the UI thread.
/// </summary>
public static class ImageTools
{
    public const int ThumbnailSize = 220;

    public static BitmapSource Decode(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return DecodeFirstFrame(stream);
    }

    public static BitmapSource LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return DecodeFirstFrame(stream);
    }

    /// <summary>Loads every frame of an image file (multi-page TIFFs have several).</summary>
    public static IReadOnlyList<BitmapSource> LoadAllFrames(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return decoder.Frames.Select(Detach).ToList();
    }

    private static BitmapSource DecodeFirstFrame(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return Detach(decoder.Frames[0]);
    }

    /// <summary>
    /// Copies an image into a plain bitmap. Decoded frames, and anything derived from them
    /// (CroppedBitmap, TransformedBitmap, ...), keep a link to their BitmapDecoder, and the
    /// decoder belongs to the thread that created it. Freezing doesn't sever that link: building
    /// a derived bitmap from such a frame on another thread throws "The calling thread cannot
    /// access this object because a different thread owns it". A copy has no such link.
    /// </summary>
    private static BitmapSource Detach(BitmapSource image)
    {
        int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return Freeze(BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            image.Format, image.Palette, pixels, stride));
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static byte[] EncodeJpeg(BitmapSource image, int quality)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A small, standalone copy of the image for the page list.</summary>
    public static BitmapSource CreateThumbnail(BitmapSource image, int maxSize = ThumbnailSize)
    {
        double scale = Math.Min(1.0, (double)maxSize / Math.Max(image.PixelWidth, image.PixelHeight));
        // Scale in a continuous-tone format so black & white pages don't turn into noise.
        BitmapSource source = new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        if (scale < 1.0)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        // WriteableBitmap copies the pixels, so the thumbnail doesn't keep the full image alive.
        return Freeze(new WriteableBitmap(source));
    }

    public static BitmapSource Rotate(BitmapSource image, int degrees) =>
        Detach(new TransformedBitmap(image, new RotateTransform(degrees)));

    public static BitmapSource Crop(BitmapSource image, Int32Rect rect) =>
        Detach(new CroppedBitmap(image, rect));

    /// <summary>Converts a normalised (0..1) rectangle to whole pixels inside the image.</summary>
    public static Int32Rect ToPixelRect(Rect normalized, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(normalized.X * width), 0, width - 1);
        int y = Math.Clamp((int)Math.Round(normalized.Y * height), 0, height - 1);
        int w = Math.Clamp((int)Math.Round(normalized.Width * width), 1, width - x);
        int h = Math.Clamp((int)Math.Round(normalized.Height * height), 1, height - y);
        return new Int32Rect(x, y, w, h);
    }

    public static (byte[] Pixels, int Stride) GetPixels(BitmapSource image, PixelFormat format)
    {
        BitmapSource source = image.Format == format ? image : new FormatConvertedBitmap(image, format, null, 0);
        int stride = (source.PixelWidth * format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return (pixels, stride);
    }

    public static BitmapSource Create(int width, int height, double dpi, PixelFormat format, byte[] pixels, int stride) =>
        Freeze(BitmapSource.Create(width, height, dpi, dpi, format, null, pixels, stride));

    /// <summary>The image's dpi, or 96 when the file carries none or a nonsense value.</summary>
    public static double ValidDpi(double dpi) => dpi is > 1 and < 10000 ? dpi : 96;

    public static bool IsBlackWhite(BitmapSource image) =>
        image.Format == PixelFormats.BlackWhite || image.Format == PixelFormats.Indexed1 && IsGrayPalette(image.Palette);

    public static bool IsGray(BitmapSource image)
    {
        var f = image.Format;
        if (f == PixelFormats.Gray2 || f == PixelFormats.Gray4 || f == PixelFormats.Gray8 ||
            f == PixelFormats.Gray16 || f == PixelFormats.Gray32Float)
            return true;
        return (f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8) && IsGrayPalette(image.Palette);
    }

    private static bool IsGrayPalette(BitmapPalette? palette) =>
        palette is not null && palette.Colors.All(c => c.R == c.G && c.G == c.B);

    /// <summary>
    /// Turns raw scanner output into the page image: applies the adjustments the device
    /// couldn't do itself, converts to the requested colour mode and checks for blank pages.
    /// </summary>
    public static ProcessedScan ProcessScan(ScannedImage scan, ScanProcessingOptions options)
    {
        var decoded = Decode(scan.Data);
        int width = decoded.PixelWidth, height = decoded.PixelHeight;
        double dpi = scan.Dpi > 0 ? scan.Dpi : decoded.DpiX;
        int brightness = scan.BrightnessApplied ? 0 : options.Brightness;
        int contrast = scan.ContrastApplied ? 0 : options.Contrast;

        if (options.ColorMode == ColorMode.Color)
        {
            var (bgr, stride) = GetPixels(decoded, PixelFormats.Bgr24);
            PixelOps.ApplyBrightnessContrast(bgr, brightness, contrast);
            bool blank = false;
            if (options.DetectBlankPage)
            {
                var gray = PixelOps.Bgr24ToGray(bgr, width, height, stride, out int grayStride);
                blank = PixelOps.IsBlank(gray, width, height, grayStride);
            }
            return new ProcessedScan(Create(width, height, dpi, PixelFormats.Bgr24, bgr, stride), blank);
        }

        var (pixels, grayRowStride) = GetPixels(decoded, PixelFormats.Gray8);
        PixelOps.ApplyBrightnessContrast(pixels, brightness, contrast);
        bool isBlank = options.DetectBlankPage && PixelOps.IsBlank(pixels, width, height, grayRowStride);

        if (options.ColorMode == ColorMode.BlackAndWhite)
        {
            var bits = PixelOps.ThresholdToOneBit(pixels, width, height, grayRowStride, options.BlackWhiteThreshold, out int bitStride);
            return new ProcessedScan(Create(width, height, dpi, PixelFormats.BlackWhite, bits, bitStride), isBlank);
        }
        return new ProcessedScan(Create(width, height, dpi, PixelFormats.Gray8, pixels, grayRowStride), isBlank);
    }

    /// <summary>Brings an imported image into one of the formats pages are stored in.</summary>
    public static BitmapSource NormalizeImported(BitmapSource image)
    {
        if (IsBlackWhite(image))
            return image.Format == PixelFormats.BlackWhite ? image : Detach(new FormatConvertedBitmap(image, PixelFormats.BlackWhite, null, 0));
        if (IsGray(image))
            return image.Format == PixelFormats.Gray8 ? image : Detach(new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0));
        return image.Format == PixelFormats.Bgr24 ? image : Detach(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0));
    }

    private static T Freeze<T>(T bitmap) where T : Freezable
    {
        if (bitmap.CanFreeze) bitmap.Freeze();
        return bitmap;
    }
}
