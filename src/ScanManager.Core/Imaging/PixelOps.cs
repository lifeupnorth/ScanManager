namespace ScanManager.Core.Imaging;

/// <summary>
/// Pixel-level operations on raw 8-bit buffers (Gray8 or Bgr24). These are used when
/// the scanner can't perform an adjustment itself, e.g. many network scanners have no
/// brightness/contrast control and no 1-bit mode.
/// </summary>
public static class PixelOps
{
    /// <summary>
    /// Applies brightness and contrast (each -100..100) to every byte of the buffer.
    /// Works for any format whose channels are 8-bit and have no alpha (Gray8, Bgr24).
    /// </summary>
    public static void ApplyBrightnessContrast(Span<byte> pixels, int brightness, int contrast)
    {
        if (brightness == 0 && contrast == 0) return;
        byte[] lut = BuildBrightnessContrastTable(brightness, contrast);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = lut[pixels[i]];
    }

    internal static byte[] BuildBrightnessContrastTable(int brightness, int contrast)
    {
        brightness = Math.Clamp(brightness, -100, 100);
        contrast = Math.Clamp(contrast, -100, 100);

        double offset = brightness * 255.0 / 200.0; // +-127.5 at the extremes
        double c = contrast * 2.55;                 // -255..255
        double factor = 259.0 * (c + 255.0) / (255.0 * (259.0 - c));

        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
        {
            double adjusted = factor * (v + offset - 128.0) + 128.0;
            lut[v] = (byte)Math.Clamp((int)Math.Round(adjusted), 0, 255);
        }
        return lut;
    }

    /// <summary>Converts a Bgr24 buffer to Gray8 using Rec. 601 luma weights.</summary>
    public static byte[] Bgr24ToGray(ReadOnlySpan<byte> bgr, int width, int height, int stride, out int grayStride)
    {
        grayStride = width;
        var gray = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            var row = bgr.Slice(y * stride, width * 3);
            int o = y * width;
            for (int x = 0; x < width; x++)
            {
                int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                gray[o + x] = (byte)((r * 299 + g * 587 + b * 114 + 500) / 1000);
            }
        }
        return gray;
    }

    /// <summary>
    /// Thresholds a Gray8 buffer into a packed 1-bit buffer (MSB first, rows padded to
    /// whole bytes). Bit value 1 is white and 0 is black, matching both WPF's BlackWhite
    /// format and PDF's 1-bit DeviceGray.
    /// </summary>
    public static byte[] ThresholdToOneBit(ReadOnlySpan<byte> gray, int width, int height, int stride, byte threshold, out int bitStride)
    {
        bitStride = (width + 7) / 8;
        var bits = new byte[bitStride * height];
        for (int y = 0; y < height; y++)
        {
            var row = gray.Slice(y * stride, width);
            int o = y * bitStride;
            for (int x = 0; x < width; x++)
            {
                if (row[x] >= threshold)
                    bits[o + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }
        return bits;
    }

    /// <summary>
    /// Heuristic blank-page detection on a Gray8 image. A margin around the edges (where
    /// shadows and feeder rollers show up) is ignored, and the rest is split into tiles.
    /// The page counts as blank only if no tile holds a noticeable amount of ink: a single
    /// line of handwriting fills a few tiles densely, whereas dust and specks are spread
    /// thinly. Looking at tiles rather than the page total is what keeps a short note on
    /// an otherwise empty back side from being thrown away.
    /// </summary>
    public static bool IsBlank(ReadOnlySpan<byte> gray, int width, int height, int stride,
        double maxTileInkRatio = 0.01, byte darkThreshold = 170, double marginFraction = 0.04)
    {
        int mx = (int)(width * marginFraction), my = (int)(height * marginFraction);
        int x0 = mx, x1 = width - mx, y0 = my, y1 = height - my;
        if (x1 <= x0 || y1 <= y0) return false;

        // About 1/24 of the shorter side: roughly 1/3 inch on a letter page.
        int tile = Math.Max(8, Math.Min(width, height) / 24);
        // Whole tiles only: the leftover strip joins the last tile rather than forming a sliver
        // a few pixels wide, where a single speck of dust would count as ink.
        int tilesX = Math.Max(1, (x1 - x0) / tile), tilesY = Math.Max(1, (y1 - y0) / tile);
        var dark = new int[tilesX * tilesY];
        var samples = new int[tilesX * tilesY];

        // Sampling every other pixel in both directions is plenty and 4x faster.
        const int step = 2;
        for (int y = y0; y < y1; y += step)
        {
            var row = gray.Slice(y * stride, width);
            int rowBase = Math.Min((y - y0) / tile, tilesY - 1) * tilesX;
            for (int x = x0; x < x1; x += step)
            {
                int t = rowBase + Math.Min((x - x0) / tile, tilesX - 1);
                samples[t]++;
                if (row[x] < darkThreshold) dark[t]++;
            }
        }

        for (int t = 0; t < dark.Length; t++)
        {
            if (samples[t] > 0 && (double)dark[t] / samples[t] > maxTileInkRatio)
                return false;
        }
        return true;
    }
}
