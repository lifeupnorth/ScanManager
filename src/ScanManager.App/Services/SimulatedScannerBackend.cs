using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Threading;

namespace ScanManager.App.Services;

/// <summary>
/// A fake scanner with a flatbed, a feeder and duplex, for trying out the app (and
/// developing it) without wasting paper. Enabled in Debug builds and with --simulator.
/// </summary>
public sealed class SimulatedScannerBackend : IScannerBackend, IDisposable
{
    public const string Id = "simulator";

    private const int SheetsInFeeder = 3;
    private static readonly SizeInches BedSize = new(8.5, 11.7);
    private static readonly SizeInches FeederSize = new(8.5, 14);
    private static readonly int[] Resolutions = [75, 100, 150, 200, 300];

    private readonly StaWorker _sta = new("Simulated scanner");

    public string BackendId => Id;

    public Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ScannerInfo>>([new ScannerInfo("simulator-1", "Test scanner (simulated)", Id)]);

    public Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ColorMode[] modes = [ColorMode.Color, ColorMode.Grayscale];
        return Task.FromResult(new ScannerCapabilities(
        [
            new SourceCapabilities(ScanSource.Flatbed, BedSize, Resolutions, modes, null, null),
            new SourceCapabilities(ScanSource.Feeder, FeederSize, Resolutions, modes, null, null),
            new SourceCapabilities(ScanSource.Duplex, FeederSize, Resolutions, modes, null, null),
        ]));
    }

    public async IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int images = settings.Source switch
        {
            ScanSource.Feeder => SheetsInFeeder,
            ScanSource.Duplex => SheetsInFeeder * 2,
            _ => 1,
        };

        int dpi = Resolutions.MinBy(r => Math.Abs(r - settings.Dpi));
        var delivered = settings.ColorMode == ColorMode.Color ? ColorMode.Color : ColorMode.Grayscale;

        for (int i = 1; i <= images; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(settings.IsPreview ? 300 : 700); // like a real page, finished even when canceled
            int index = i;
            byte[] data = await _sta.InvokeAsync(() => Render(settings, dpi, index));
            yield return new ScannedImage(data, dpi, i, delivered, BrightnessApplied: false, ContrastApplied: false);
        }
    }

    public void Dispose() => _sta.Dispose();

    private static byte[] Render(ScanSettings settings, int dpi, int imageNumber)
    {
        bool feeder = settings.Source != ScanSource.Flatbed;
        var max = feeder ? FeederSize : BedSize;
        var area = (settings.Area ?? ScanArea.Full(max)).ClampTo(max);
        if (feeder) area = area with { X = 0, Y = 0 };
        var px = area.ToPixels(dpi);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-area.X * 96, -area.Y * 96));
            if (feeder)
            {
                int sheet = settings.Source == ScanSource.Duplex ? (imageNumber + 1) / 2 : imageNumber;
                bool back = settings.Source == ScanSource.Duplex && imageNumber % 2 == 0;
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, max.Width * 96, max.Height * 96));
                if (back)
                    DrawBackSide(dc, sheet, dpi);
                else
                    DrawDocument(dc, sheet, SheetsInFeeder, dpi);
            }
            else
            {
                // The underside of the lid, with a letter page laid on the glass.
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(236, 236, 232)), null, new Rect(0, 0, max.Width * 96, max.Height * 96));
                dc.PushTransform(new TranslateTransform(0.15 * 96, 0.15 * 96));
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 8.2 * 96, 10.9 * 96));
                DrawDocument(dc, 1, 1, dpi);
                dc.Pop();
            }
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(px.Width, px.Height, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        BitmapSource output = settings.ColorMode == ColorMode.Color
            ? new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0)
            : new FormatConvertedBitmap(bitmap, PixelFormats.Gray8, null, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(output));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void DrawDocument(DrawingContext dc, int page, int pageCount, int dpi)
    {
        const double inch = 96;
        double pixelsPerDip = dpi / 96.0;
        var accent = new SolidColorBrush(Color.FromRgb(0, 99, 177));
        var ink = new SolidColorBrush(Color.FromRgb(40, 40, 40));
        var faint = new SolidColorBrush(Color.FromRgb(120, 120, 120));

        // Letterhead: a coloured logo so colour/grayscale/B&W differences are visible.
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(232, 72, 39)), null, new Point(1.2 * inch, 1.2 * inch), 0.35 * inch, 0.35 * inch);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(255, 185, 0)), null, new Point(1.55 * inch, 1.2 * inch), 0.25 * inch, 0.25 * inch);
        Text(dc, "Contoso Paper Works", 1.95 * inch, 0.95 * inch, 22, accent, pixelsPerDip, bold: true);
        Text(dc, "123 Example Street · Springfield", 1.95 * inch, 1.35 * inch, 11, faint, pixelsPerDip);
        dc.DrawRectangle(accent, null, new Rect(0.8 * inch, 1.75 * inch, 6.6 * inch, 0.03 * inch));

        Text(dc, page == 1 ? "Simulated scan" : $"Simulated scan, continued", 0.8 * inch, 2.0 * inch, 18, ink, pixelsPerDip, bold: true);
        Text(dc, DateTime.Today.ToString("D", CultureInfo.CurrentCulture), 0.8 * inch, 2.4 * inch, 11, faint, pixelsPerDip);

        string[] paragraphs =
        [
            "This page was produced by the ScanManager test scanner. It behaves like a real device with a flatbed, an automatic document feeder and duplex scanning, so every feature of the app can be tried without a scanner attached.",
            "Use Preview to scan the whole bed at low resolution, then drag the frame to choose the area to scan. Paper size presets snap the frame to standard sizes. Pages fed through the feeder can be cropped, rotated and reordered after scanning.",
            "When scanning both sides, the back of the first sheet is left blank on purpose: turn on 'Skip blank pages' and it will be dropped automatically.",
        ];
        double y = 2.9 * inch;
        foreach (var paragraph in paragraphs)
            y = Paragraph(dc, paragraph, 0.8 * inch, y, 6.6 * inch, ink, pixelsPerDip) + 0.2 * inch;

        // A small table, to give the crop tool something to frame.
        var linePen = new Pen(faint, 1);
        string[,] rows = { { "Item", "Qty", "Amount" }, { "Recycled paper, A4", "4", "$23.60" }, { "Toner cartridge", "1", "$89.00" }, { "Total", "", "$112.60" } };
        double top = y + 0.1 * inch, rowHeight = 0.32 * inch;
        double[] cols = [0.8 * inch, 4.8 * inch, 6.1 * inch];
        for (int r = 0; r < rows.GetLength(0); r++)
        {
            double ry = top + r * rowHeight;
            if (r == 0) dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(221, 235, 247)), null, new Rect(0.8 * inch, ry, 6.6 * inch, rowHeight));
            for (int c = 0; c < 3; c++)
                Text(dc, rows[r, c], cols[c] + 0.08 * inch, ry + 0.06 * inch, 11, ink, pixelsPerDip, bold: r == 0 || r == 3);
            dc.DrawLine(linePen, new Point(0.8 * inch, ry + rowHeight), new Point(7.4 * inch, ry + rowHeight));
        }

        double signY = top + rows.GetLength(0) * rowHeight + 0.9 * inch;
        dc.DrawLine(new Pen(ink, 1), new Point(4.6 * inch, signY), new Point(7.4 * inch, signY));
        Text(dc, "Signature", 4.6 * inch, signY + 0.05 * inch, 10, faint, pixelsPerDip);

        Text(dc, $"Page {page} of {pageCount}", 3.7 * inch, 10.3 * inch, 10, faint, pixelsPerDip);
    }

    private static void DrawBackSide(DrawingContext dc, int sheet, int dpi)
    {
        if (sheet == 1) return; // intentionally blank, for blank-page skipping
        Text(dc, $"Back of sheet {sheet}: handwritten notes", 0.8 * 96, 1.0 * 96, 14,
            new SolidColorBrush(Color.FromRgb(30, 60, 140)), dpi / 96.0);
    }

    private static void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, double pixelsPerDip, bool bold = false)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size * 96 / 72, brush, pixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static double Paragraph(DrawingContext dc, string text, double x, double y, double width, Brush brush, double pixelsPerDip)
    {
        var typeface = new Typeface("Segoe UI");
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12 * 96 / 72.0, brush, pixelsPerDip)
        {
            MaxTextWidth = width,
            LineHeight = 12 * 96 / 72.0 * 1.4,
        };
        dc.DrawText(formatted, new Point(x, y));
        return y + formatted.Height;
    }
}
