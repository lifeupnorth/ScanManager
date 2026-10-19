using ScanManager.Core.Geometry;

namespace ScanManager.Core.Scanning;

/// <summary>An inclusive integer range supported by the device (e.g. brightness).</summary>
public readonly record struct ValueRange(int Min, int Max)
{
    public bool IsAdjustable => Max > Min;

    /// <summary>Maps a UI value in -100..100 onto this range, with 0 mapping to the middle.</summary>
    public int MapFromPercent(int percent)
    {
        percent = Math.Clamp(percent, -100, 100);
        double mid = (Min + Max) / 2.0;
        double value = percent >= 0
            ? mid + percent / 100.0 * (Max - mid)
            : mid + percent / 100.0 * (mid - Min);
        return (int)Math.Round(value);
    }
}

/// <summary>What the device can do when scanning from a particular source.</summary>
public sealed record SourceCapabilities(
    ScanSource Source,
    SizeInches MaxSize,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<ColorMode> NativeColorModes,
    ValueRange? Brightness,
    ValueRange? Contrast)
{
    /// <summary>The lowest resolution suitable for a quick preview scan (ideally 75-100 dpi).</summary>
    public int PreviewResolution
    {
        get
        {
            if (Resolutions.Count == 0) return 100;
            var usable = Resolutions.Where(r => r >= 50).DefaultIfEmpty(Resolutions.Max()).ToList();
            var low = usable.Where(r => r <= 100).ToList();
            return low.Count > 0 ? low.Max() : usable.Min();
        }
    }
}

public sealed record ScannerCapabilities(IReadOnlyList<SourceCapabilities> Sources)
{
    public SourceCapabilities? For(ScanSource source) => Sources.FirstOrDefault(s => s.Source == source);
}
