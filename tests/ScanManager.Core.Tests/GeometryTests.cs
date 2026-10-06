using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;

namespace ScanManager.Core.Tests;

public class GeometryTests
{
    private static readonly SizeInches Bed = new(8.5, 14);

    [Fact]
    public void ClampTo_keeps_an_area_that_already_fits()
    {
        var area = new ScanArea(1, 2, 3, 4);
        Assert.Equal(area, area.ClampTo(Bed));
    }

    [Fact]
    public void ClampTo_moves_an_overhanging_area_back_inside()
    {
        var clamped = new ScanArea(7, 12, 3, 4).ClampTo(Bed);
        Assert.Equal(new ScanArea(5.5, 10, 3, 4), clamped);
    }

    [Fact]
    public void ClampTo_shrinks_an_area_larger_than_the_bed()
    {
        var clamped = new ScanArea(0, 0, 20, 20).ClampTo(Bed);
        Assert.Equal(ScanArea.Full(Bed), clamped);
    }

    [Fact]
    public void ClampTo_enforces_a_minimum_size()
    {
        var clamped = new ScanArea(1, 1, 0.01, 0).ClampTo(Bed);
        Assert.Equal(ScanArea.MinimumSize, clamped.Width);
        Assert.Equal(ScanArea.MinimumSize, clamped.Height);
    }

    [Fact]
    public void Normalized_round_trip_preserves_the_area()
    {
        var area = new ScanArea(0.5, 1.25, 4, 6);
        var n = area.ToNormalized(Bed);
        var back = ScanArea.FromNormalized(n.X, n.Y, n.Width, n.Height, Bed);
        Assert.True(area.ApproximatelyEquals(back, 1e-9));
    }

    [Fact]
    public void ToPixels_converts_inches_at_the_resolution()
    {
        var px = new ScanArea(1, 0.5, 8.5, 11).ToPixels(300);
        Assert.Equal(new PixelRect(300, 150, 2550, 3300), px);
    }

    [Fact]
    public void A4_is_converted_from_millimetres()
    {
        Assert.Equal(8.27, PaperSize.A4.Size.Width, 2);
        Assert.Equal(11.69, PaperSize.A4.Size.Height, 2);
    }

    [Fact]
    public void Paper_sizes_that_exceed_the_bed_do_not_fit()
    {
        var letterBed = new SizeInches(8.5, 11.7);
        Assert.True(PaperSize.A4.Size.Fits(letterBed));
        Assert.False(PaperSize.Legal.Size.Fits(letterBed));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 1000)]
    [InlineData(-100, -1000)]
    [InlineData(50, 500)]
    public void ValueRange_maps_percent_symmetrically(int percent, int expected)
    {
        Assert.Equal(expected, new ValueRange(-1000, 1000).MapFromPercent(percent));
    }

    [Fact]
    public void ValueRange_maps_zero_to_the_middle_of_an_asymmetric_range()
    {
        var range = new ValueRange(0, 255);
        Assert.Equal(128, range.MapFromPercent(0));
        Assert.Equal(0, range.MapFromPercent(-100));
        Assert.Equal(255, range.MapFromPercent(100));
    }

    [Theory]
    [InlineData(new[] { 75, 100, 150, 300, 600 }, 100)]
    [InlineData(new[] { 150, 300, 600 }, 150)]
    [InlineData(new[] { 300 }, 300)]
    [InlineData(new[] { 25, 50, 75 }, 75)]
    public void Preview_uses_the_highest_resolution_up_to_100_dpi(int[] resolutions, int expected)
    {
        var caps = new SourceCapabilities(ScanSource.Flatbed, Bed, resolutions, [ColorMode.Color], null, null);
        Assert.Equal(expected, caps.PreviewResolution);
    }

    [Fact]
    public void ClosestResolution_snaps_to_a_supported_value()
    {
        var caps = new SourceCapabilities(ScanSource.Flatbed, Bed, [150, 300, 600], [ColorMode.Color], null, null);
        Assert.Equal(300, caps.ClosestResolution(400));
        Assert.Equal(150, caps.ClosestResolution(72));
    }

    [Theory]
    [InlineData("Canon MF741C/743C (d5:97:d9) (2) (d5:97:d9) (d5  (d5:97:d9) (2)", "Canon MF741C/743C (d5:97:d9) (2)")]
    [InlineData("HP ScanJet Pro 2500", "HP ScanJet Pro 2500")]
    [InlineData("Epson (USB)", "Epson (USB)")]
    [InlineData("  ", "Unknown scanner")]
    public void Device_names_lose_repeated_suffixes(string raw, string expected)
    {
        Assert.Equal(expected, DeviceNames.Clean(raw));
    }
}
