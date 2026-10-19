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
    /// <param name="replaceNumbered">Overwrite existing name_001… files; without it a multi-page image save refuses to.</param>
    public static IReadOnlyList<string> Save(IReadOnlyList<string> pageFiles, string path, ExportFormat format, int jpegQuality,
        bool replaceNumbered = false)
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
                return SaveImages(pageFiles, path, format, jpegQuality, replaceNumbered);
        }
    }

    internal static PdfPageImage ToPdfImage(BitmapSource image, int jpegQuality)
    {
        double dpiX = ImageTools.ValidDpi(image.DpiX), dpiY = ImageTools.ValidDpi(image.DpiY);

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
        // Undecoded frames: the encoder decodes each page as it writes it, so only one page is in
        // memory at a time instead of all of them (a 30-page color job went from 830 MB to under 50 MB).
        var streams = new List<FileStream>();
        try
        {
            // Opened inside the try, so a missing page still closes the files opened before it.
            foreach (var file in pageFiles)
                streams.Add(File.OpenRead(file));
            var frames = streams.Select(s => BitmapDecoder.Create(s,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None).Frames[0]).ToList();
            var encoder = new TiffBitmapEncoder
            {
                // CCITT G4 is the standard for black & white documents; it only accepts 1-bit frames.
                Compression = frames.All(ImageTools.IsBlackWhite) ? TiffCompressOption.Ccitt4 : TiffCompressOption.Lzw,
            };
            foreach (var frame in frames)
                encoder.Frames.Add(frame);
            WriteAtomically(path, encoder.Save);
        }
        finally
        {
            foreach (var stream in streams) stream.Dispose();
        }
    }

    /// <summary>
    /// The numbered files a multi-page PNG/JPEG save to <paramref name="path"/> would overwrite. The Save
    /// dialog only asks about the name the user typed, so the caller must ask about these.
    /// </summary>
    public static IReadOnlyList<string> ExistingNumberedFiles(string path, int pageCount) =>
        pageCount > 1 ? Enumerable.Range(1, pageCount).Select(i => NumberedPath(path, i)).Where(File.Exists).ToList() : [];

    private static List<string> SaveImages(IReadOnlyList<string> pageFiles, string path, ExportFormat format, int jpegQuality,
        bool replaceNumbered)
    {
        if (!replaceNumbered && ExistingNumberedFiles(path, pageFiles.Count) is [var existing, ..])
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
