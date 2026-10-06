using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace ScanManager.Core.Pdf;

public enum PdfImageEncoding
{
    /// <summary>Data is a complete JPEG file, embedded as-is (DCTDecode).</summary>
    Jpeg,
    /// <summary>Data is raw, uncompressed samples; the writer deflates it (FlateDecode).</summary>
    Raw,
}

public enum PdfColorSpace
{
    Gray,
    Rgb,
}

/// <summary>One page of a PDF consisting of a single full-page image.</summary>
public sealed record PdfPageImage(
    byte[] Data,
    PdfImageEncoding Encoding,
    PdfColorSpace ColorSpace,
    int BitsPerComponent,
    int PixelWidth,
    int PixelHeight,
    double DpiX,
    double DpiY)
{
    public double WidthPoints => PixelWidth / DpiX * 72.0;
    public double HeightPoints => PixelHeight / DpiY * 72.0;
}

/// <summary>
/// Minimal PDF 1.4 writer for image-only documents (what a scanner produces). Pages are
/// written as they are enumerated so a large scan never has to be held in memory at once.
/// </summary>
public static class PdfWriter
{
    private const int CatalogId = 1;
    private const int PagesId = 2;
    private const int InfoId = 3;
    private const int FirstPageObjectId = 4;
    private const int ObjectsPerPage = 3; // page, content stream, image

    public static void Write(Stream output, IEnumerable<PdfPageImage> pages, string? title = null, DateTimeOffset? created = null)
    {
        var w = new CountingWriter(output);
        var offsets = new SortedDictionary<int, long>();
        var pageIds = new List<int>();

        w.Ascii("%PDF-1.4\n");
        // Binary marker so transfer tools treat the file as binary.
        w.Bytes([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        offsets[CatalogId] = w.Position;
        w.Ascii($"{CatalogId} 0 obj\n<< /Type /Catalog /Pages {PagesId} 0 R >>\nendobj\n");

        int nextId = FirstPageObjectId;
        foreach (var page in pages)
        {
            int pageId = nextId, contentId = nextId + 1, imageId = nextId + 2;
            nextId += ObjectsPerPage;
            pageIds.Add(pageId);

            string wPt = Num(page.WidthPoints), hPt = Num(page.HeightPoints);

            offsets[pageId] = w.Position;
            w.Ascii($"{pageId} 0 obj\n<< /Type /Page /Parent {PagesId} 0 R /MediaBox [0 0 {wPt} {hPt}] " +
                    $"/Resources << /XObject << /Im0 {imageId} 0 R >> >> /Contents {contentId} 0 R >>\nendobj\n");

            byte[] content = Encoding.ASCII.GetBytes($"q {wPt} 0 0 {hPt} 0 0 cm /Im0 Do Q\n");
            offsets[contentId] = w.Position;
            w.Ascii($"{contentId} 0 obj\n<< /Length {content.Length} >>\nstream\n");
            w.Bytes(content);
            w.Ascii("\nendstream\nendobj\n");

            byte[] data;
            string filter;
            if (page.Encoding == PdfImageEncoding.Jpeg)
            {
                data = page.Data;
                filter = "/DCTDecode";
            }
            else
            {
                data = Deflate(page.Data);
                filter = "/FlateDecode";
            }

            string colorSpace = page.ColorSpace == PdfColorSpace.Rgb ? "/DeviceRGB" : "/DeviceGray";
            offsets[imageId] = w.Position;
            w.Ascii($"{imageId} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} " +
                    $"/ColorSpace {colorSpace} /BitsPerComponent {page.BitsPerComponent} /Filter {filter} /Length {data.Length} >>\nstream\n");
            w.Bytes(data);
            w.Ascii("\nendstream\nendobj\n");
        }

        if (pageIds.Count == 0)
            throw new InvalidOperationException("A PDF needs at least one page.");

        offsets[PagesId] = w.Position;
        string kids = string.Join(' ', pageIds.Select(id => $"{id} 0 R"));
        w.Ascii($"{PagesId} 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageIds.Count} >>\nendobj\n");

        var date = (created ?? DateTimeOffset.Now).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var info = new StringBuilder($"<< /Producer {TextString("ScanManager")} /CreationDate (D:{date})");
        if (!string.IsNullOrWhiteSpace(title)) info.Append($" /Title {TextString(title)}");
        info.Append(" >>");
        offsets[InfoId] = w.Position;
        w.Ascii($"{InfoId} 0 obj\n{info}\nendobj\n");

        long xref = w.Position;
        int size = nextId;
        var sb = new StringBuilder();
        sb.Append($"xref\n0 {size}\n");
        sb.Append("0000000000 65535 f \n");
        for (int id = 1; id < size; id++)
            sb.Append(CultureInfo.InvariantCulture, $"{offsets[id]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {size} /Root {CatalogId} 0 R /Info {InfoId} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        w.Ascii(sb.ToString());
        output.Flush();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Encodes text as a UTF-16BE hex string, which handles any language.</summary>
    internal static string TextString(string text)
    {
        var sb = new StringBuilder("<FEFF");
        foreach (byte b in Encoding.BigEndianUnicode.GetBytes(text))
            sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
        return sb.Append('>').ToString();
    }

    private sealed class CountingWriter(Stream stream)
    {
        public long Position { get; private set; }

        public void Ascii(string text) => Bytes(Encoding.ASCII.GetBytes(text));

        public void Bytes(ReadOnlySpan<byte> bytes)
        {
            stream.Write(bytes);
            Position += bytes.Length;
        }
    }
}
