using ScanManager.Core.Geometry;

namespace ScanManager.App.ViewModels;

/// <summary>An entry of the scan-area dropdown: the whole bed, a paper size, or a custom frame.</summary>
public sealed record AreaPreset(string Name, SizeInches? PaperSize, bool IsCustom = false)
{
    public const string FullAreaName = "Entire scan area";
    public const string CustomName = "Custom (drag on preview)";

    public static AreaPreset FullArea { get; } = new(FullAreaName, null);
    public static AreaPreset Custom { get; } = new(CustomName, null, IsCustom: true);

    /// <summary>The area this preset selects; paper is assumed to be aligned to the top-left corner.</summary>
    public ScanArea AreaFor(SizeInches bed) => PaperSize is { } size
        ? new ScanArea(0, 0, size.Width, size.Height).ClampTo(bed)
        : ScanArea.Full(bed);

    public override string ToString() => Name;
}
