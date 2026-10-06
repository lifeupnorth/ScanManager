using ScanManager.Core.Imaging;

namespace ScanManager.Core.Tests;

public class PixelOpsTests
{
    [Fact]
    public void Zero_adjustments_leave_pixels_unchanged()
    {
        byte[] pixels = [0, 64, 128, 200, 255];
        byte[] copy = [.. pixels];
        PixelOps.ApplyBrightnessContrast(pixels, 0, 0);
        Assert.Equal(copy, pixels);
    }

    [Fact]
    public void Brightness_lightens_and_darkens()
    {
        byte[] lighter = [100];
        byte[] darker = [100];
        PixelOps.ApplyBrightnessContrast(lighter, 50, 0);
        PixelOps.ApplyBrightnessContrast(darker, -50, 0);
        Assert.True(lighter[0] > 100);
        Assert.True(darker[0] < 100);
    }

    [Fact]
    public void Contrast_spreads_values_away_from_mid_gray()
    {
        byte[] pixels = [100, 160];
        PixelOps.ApplyBrightnessContrast(pixels, 0, 50);
        Assert.True(pixels[0] < 100);
        Assert.True(pixels[1] > 160);
    }

    [Fact]
    public void Adjustment_table_is_monotonic_and_saturates()
    {
        var lut = PixelOps.BuildBrightnessContrastTable(30, 80);
        for (int i = 1; i < 256; i++)
            Assert.True(lut[i] >= lut[i - 1]);
        Assert.Equal(0, lut[0]);
        Assert.Equal(255, lut[255]);
    }

    [Fact]
    public void Bgr_to_gray_uses_luma_weights()
    {
        // One pixel each: pure blue, pure green, pure red, white (BGR order).
        byte[] bgr = [255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255];
        var gray = PixelOps.Bgr24ToGray(bgr, 4, 1, 12, out int stride);
        Assert.Equal(4, stride);
        Assert.Equal([29, 150, 76, 255], gray);
    }

    [Fact]
    public void Threshold_packs_bits_msb_first_with_white_as_one()
    {
        // 10 pixels: alternating dark/light, so rows need 2 bytes.
        byte[] gray = [0, 255, 0, 255, 0, 255, 0, 255, 255, 0];
        var bits = PixelOps.ThresholdToOneBit(gray, 10, 1, 10, 128, out int bitStride);
        Assert.Equal(2, bitStride);
        Assert.Equal(0b0101_0101, bits[0]);
        Assert.Equal(0b1000_0000, bits[1]);
    }

    [Fact]
    public void Threshold_respects_row_stride()
    {
        // 2x2 image stored with a padded stride of 4.
        byte[] gray = [255, 0, 9, 9, 0, 255, 9, 9];
        var bits = PixelOps.ThresholdToOneBit(gray, 2, 2, 4, 128, out _);
        Assert.Equal([0b1000_0000, 0b0100_0000], bits);
    }

    [Fact]
    public void A_white_page_with_edge_shadows_is_blank()
    {
        const int w = 200, h = 300;
        var page = Filled(w, h, 250);
        // Dark border from the feeder rollers, inside the ignored margin.
        for (int x = 0; x < w; x++) { page[x] = 0; page[(h - 1) * w + x] = 0; }
        Assert.True(PixelOps.IsBlank(page, w, h, w));
    }

    [Fact]
    public void A_page_with_text_is_not_blank()
    {
        const int w = 200, h = 300;
        var page = Filled(w, h, 250);
        // A block of "text" covering about 2% of the page.
        for (int y = 100; y < 130; y++)
            for (int x = 40; x < 80; x++)
                page[y * w + x] = 20;
        Assert.False(PixelOps.IsBlank(page, w, h, w));
    }

    [Fact]
    public void A_single_short_line_of_text_is_not_blank()
    {
        // Letter page at 100 dpi with one 2-inch line of text: about 0.05% ink overall,
        // which a whole-page ratio would wrongly call blank.
        const int w = 850, h = 1100;
        var page = Filled(w, h, 250);
        for (int y = 200; y < 214; y++)
            for (int x = 100; x < 300; x++)
                if ((x / 3 + y) % 3 == 0) page[y * w + x] = 30; // strokes, not a solid bar
        Assert.False(PixelOps.IsBlank(page, w, h, w));
    }

    [Fact]
    public void Scattered_dust_specks_are_still_blank()
    {
        const int w = 850, h = 1100;
        var page = Filled(w, h, 250);
        var random = new Random(42);
        for (int i = 0; i < 60; i++)
        {
            int cx = random.Next(50, w - 50), cy = random.Next(50, h - 50);
            for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    page[(cy + dy) * w + cx + dx] = 40;
        }
        Assert.True(PixelOps.IsBlank(page, w, h, w));
    }

    [Fact]
    public void Light_show_through_does_not_count_as_content()
    {
        const int w = 200, h = 300;
        var page = Filled(w, h, 250);
        for (int y = 50; y < 250; y++)
            for (int x = 30; x < 170; x++)
                page[y * w + x] = 215; // faint print from the other side of thin paper
        Assert.True(PixelOps.IsBlank(page, w, h, w));
    }

    private static byte[] Filled(int w, int h, byte value)
    {
        var buffer = new byte[w * h];
        Array.Fill(buffer, value);
        return buffer;
    }
}
