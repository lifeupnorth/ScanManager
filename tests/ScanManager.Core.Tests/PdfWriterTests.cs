using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ScanManager.Core.Pdf;

namespace ScanManager.Core.Tests;

public class PdfWriterTests
{
    private static readonly byte[] FakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];

    private static PdfPageImage JpegPage(int w = 2550, int h = 3300, double dpi = 300) =>
        new(FakeJpeg, PdfImageEncoding.Jpeg, PdfColorSpace.Rgb, 8, w, h, dpi, dpi);

    private static string WritePdf(params PdfPageImage[] pages)
    {
        using var stream = new MemoryStream();
        PdfWriter.Write(stream, pages, "Test", new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        // Latin1 maps every byte to one char, so string offsets equal byte offsets.
        return Encoding.Latin1.GetString(stream.ToArray());
    }

    [Fact]
    public void Writes_header_trailer_and_page_count()
    {
        string pdf = WritePdf(JpegPage(), JpegPage());
        Assert.StartsWith("%PDF-1.4\n", pdf);
        Assert.EndsWith("%%EOF\n", pdf);
        Assert.Contains("/Type /Pages /Kids [4 0 R 7 0 R] /Count 2", pdf);
    }

    [Fact]
    public void Xref_offsets_point_at_their_objects()
    {
        string pdf = WritePdf(JpegPage(), JpegPage(), JpegPage());

        int startXref = int.Parse(Regex.Match(pdf, @"startxref\n(\d+)\n").Groups[1].Value);
        Assert.StartsWith("xref\n", pdf[startXref..]);

        var entries = Regex.Matches(pdf[startXref..], @"(\d{10}) 00000 n \n");
        Assert.Equal(3 + 3 * 3, entries.Count); // catalog, pages, info + 3 objects per page
        for (int i = 0; i < entries.Count; i++)
        {
            int offset = int.Parse(entries[i].Groups[1].Value);
            Assert.StartsWith($"{i + 1} 0 obj", pdf[offset..]);
        }
    }

    [Fact]
    public void Xref_entries_are_exactly_twenty_bytes()
    {
        string pdf = WritePdf(JpegPage());
        int xref = int.Parse(Regex.Match(pdf, @"startxref\n(\d+)\n").Groups[1].Value);
        string section = pdf[xref..pdf.IndexOf("trailer", xref, StringComparison.Ordinal)];
        var lines = section.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(2);
        Assert.All(lines, line => Assert.Equal(19, line.Length)); // plus '\n' = 20
    }

    [Fact]
    public void Page_size_comes_from_pixels_and_resolution()
    {
        string pdf = WritePdf(JpegPage(2550, 3300, 300));
        Assert.Contains("/MediaBox [0 0 612 792]", pdf); // US Letter in points
    }

    [Fact]
    public void Jpeg_data_is_embedded_unchanged()
    {
        string pdf = WritePdf(JpegPage());
        Assert.Contains("/Filter /DCTDecode", pdf);
        Assert.Contains(Encoding.Latin1.GetString(FakeJpeg), pdf);
    }

    [Fact]
    public void Raw_one_bit_images_are_deflated()
    {
        byte[] bits = new byte[125 * 1000]; // 1000 x 1000 white-ish bitmap
        Array.Fill(bits, (byte)0xFF);
        var page = new PdfPageImage(bits, PdfImageEncoding.Raw, PdfColorSpace.Gray, 1, 1000, 1000, 200, 200);

        using var stream = new MemoryStream();
        PdfWriter.Write(stream, [page]);
        string pdf = Encoding.Latin1.GetString(stream.ToArray());

        Assert.Contains("/ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /FlateDecode", pdf);
        var match = Regex.Match(pdf, @"/FlateDecode /Length (\d+) >>\nstream\n");
        int length = int.Parse(match.Groups[1].Value);
        Assert.True(length < bits.Length / 10);

        byte[] compressed = stream.ToArray().AsSpan(match.Index + match.Length, length).ToArray();
        using var z = new ZLibStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        Assert.Equal(bits, output.ToArray());
    }

    [Fact]
    public void Title_is_encoded_as_unicode()
    {
        Assert.Equal("<FEFF00C9>", PdfWriter.TextString("É"));
    }

    [Fact]
    public void An_empty_document_is_rejected()
    {
        using var stream = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => PdfWriter.Write(stream, []));
    }
}
