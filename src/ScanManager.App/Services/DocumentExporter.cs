using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanManager.App.Imaging;
using ScanManager.Core.Pdf;

namespace ScanManager.App.Services;

/// <summary>File formats offered by the Save dialog. Values match the dialog's 1-based filter index.</summary>
public enum ExportFormat
{
    Pdf = 1,
    Tiff = 2,
    Png = 3,
    Jpeg = 4,
}

public static class DocumentExporter
{
    public const string DialogFilter =
        "PDF document (*.pdf)|*.pdf|" +
        "Multi-page TIFF (*.tif)|*.tif;*.tiff|" +
        "PNG images, one per page (*.png)|*.png|" +
        "JPEG images, one per page (*.jpg)|*.jpg;*.jpeg";

    public static string Extension(ExportFormat format) => format switch
    {
        ExportFormat.Pdf => ".pdf",
        ExportFormat.Tiff => ".tif",
        ExportFormat.Png => ".png",
        _ => ".jpg",
    };

    public static ExportFormat? FormatFromExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".pdf" => ExportFormat.Pdf,
        ".tif" or ".tiff" => ExportFormat.Tiff,
        ".png" => ExportFormat.Png,
        ".jpg" or ".jpeg" => ExportFormat.Jpeg,
        _ => null,
    };

    /// <summary>Saves the pages and returns the files written.</summary>
    public static IReadOnlyList<string> Save(IReadOnlyList<string> pageFiles, string path, ExportFormat format, int jpegQuality)
    {
        if (pageFiles.Count == 0) throw new InvalidOperationException("There are no pages to save.");

        switch (format)
        {
            case ExportFormat.Pdf:
                WriteAtomically(path, stream => PdfWriter.Write(stream,
                    pageFiles.Select(f => ToPdfImage(ImageTools.LoadFile(f), jpegQuality)),
                    Path.GetFileNameWithoutExtension(path)));
                return [path];

            case ExportFormat.Tiff:
                SaveTiff(pageFiles, path);
                return [path];

            default:
                return SaveImages(pageFiles, path, format, jpegQuality);
        }
    }

    internal static PdfPageImage ToPdfImage(BitmapSource image, int jpegQuality)
    {
        double dpiX = ValidDpi(image.DpiX), dpiY = ValidDpi(image.DpiY);

        if (ImageTools.IsBlackWhite(image))
        {
            // 1-bit pages stay lossless and tiny with Flate compression.
            var (bits, _) = ImageTools.GetPixels(image, PixelFormats.BlackWhite);
            return new PdfPageImage(bits, PdfImageEncoding.Raw, PdfColorSpace.Gray, 1, image.PixelWidth, image.PixelHeight, dpiX, dpiY);
        }

        bool gray = ImageTools.IsGray(image);
        BitmapSource source = new FormatConvertedBitmap(image, gray ? PixelFormats.Gray8 : PixelFormats.Bgr24, null, 0);
        byte[] jpeg = ImageTools.EncodeJpeg(source, jpegQuality);
        return new PdfPageImage(jpeg, PdfImageEncoding.Jpeg, gray ? PdfColorSpace.Gray : PdfColorSpace.Rgb, 8,
            image.PixelWidth, image.PixelHeight, dpiX, dpiY);
    }

    private static void SaveTiff(IReadOnlyList<string> pageFiles, string path)
    {
        var images = pageFiles.Select(ImageTools.LoadFile).ToList();
        var encoder = new TiffBitmapEncoder
        {
            // CCITT G4 is the standard for black & white documents; it only accepts 1-bit frames.
            Compression = images.All(ImageTools.IsBlackWhite) ? TiffCompressOption.Ccitt4 : TiffCompressOption.Lzw,
        };
        foreach (var image in images)
            encoder.Frames.Add(BitmapFrame.Create(image));
        WriteAtomically(path, encoder.Save);
    }

    private static List<string> SaveImages(IReadOnlyList<string> pageFiles, string path, ExportFormat format, int jpegQuality)
    {
        // The Save dialog only asks before replacing the name the user typed, not the numbered files.
        if (pageFiles.Count > 1 &&
            Enumerable.Range(1, pageFiles.Count).Select(i => NumberedPath(path, i)).FirstOrDefault(File.Exists) is { } existing)
            throw new IOException($"{Path.GetFileName(existing)} already exists. Choose another name so it isn't replaced.");

        var written = new List<string>();
        for (int i = 0; i < pageFiles.Count; i++)
        {
            string target = pageFiles.Count == 1 ? path : NumberedPath(path, i + 1);
            var image = ImageTools.LoadFile(pageFiles[i]);
            BitmapEncoder encoder = format == ExportFormat.Png
                ? new PngBitmapEncoder()
                : new JpegBitmapEncoder { QualityLevel = jpegQuality };
            // JPEG has no 1-bit mode; store black & white pages as 8-bit gray.
            if (format == ExportFormat.Jpeg && ImageTools.IsBlackWhite(image))
                image = new FormatConvertedBitmap(image, PixelFormats.Gray8, null, 0);
            encoder.Frames.Add(BitmapFrame.Create(image));
            WriteAtomically(target, encoder.Save);
            written.Add(target);
        }
        return written;
    }

    internal static string NumberedPath(string path, int page) => Path.Combine(
        Path.GetDirectoryName(path) ?? "",
        $"{Path.GetFileNameWithoutExtension(path)}_{page:000}{Path.GetExtension(path)}");

    private static double ValidDpi(double dpi) => dpi is > 1 and < 10000 ? dpi : 96;

    /// <summary>Writes to a temporary file first so a failure never leaves a half-written document behind.</summary>
    private static void WriteAtomically(string path, Action<Stream> write)
    {
        string temp = path + ".partial";
        try
        {
            using (var stream = File.Create(temp))
                write(stream);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
