using ScanManager.Core.Scanning;
using static ScanManager.Core.Scanning.ScanSource;

namespace ScanManager.Core.Tests;

public class SourcePreferenceTests
{
    private static readonly ScanSource[] Everything = [Flatbed, Feeder, Duplex];

    [Theory]
    [InlineData(Feeder)]
    [InlineData(Duplex)]
    [InlineData(Flatbed)]
    public void The_default_wins_when_the_scanner_has_it(ScanSource preferred)
    {
        Assert.Equal(preferred, SourcePreference.Choose(Everything, preferred, Flatbed));
    }

    [Fact]
    public void Without_a_default_the_current_source_is_kept()
    {
        Assert.Equal(Duplex, SourcePreference.Choose(Everything, null, Duplex));
    }

    [Fact]
    public void Both_sides_falls_back_to_one_side()
    {
        Assert.Equal(Feeder, SourcePreference.Choose([Flatbed, Feeder], Duplex, Flatbed));
    }

    [Fact]
    public void Feeder_falls_back_to_the_glass_on_a_flatbed_only_scanner()
    {
        Assert.Equal(Flatbed, SourcePreference.Choose([Flatbed], Feeder, Feeder));
    }

    [Fact]
    public void A_feeder_only_scanner_uses_its_feeder_even_for_a_flatbed_default()
    {
        Assert.Equal(Feeder, SourcePreference.Choose([Feeder], Flatbed, Flatbed));
    }
}
