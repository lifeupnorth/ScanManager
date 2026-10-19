using ScanManager.Core.Geometry;

namespace ScanManager.Core.Scanning;

/// <summary>Parameters of a single scan job.</summary>
public sealed record ScanSettings
{
    public ScanSource Source { get; init; } = ScanSource.Flatbed;
    public ColorMode ColorMode { get; init; } = ColorMode.Color;
    public int Dpi { get; init; } = 300;

    /// <summary>Area to scan in inches, relative to the top-left of the bed / page. Null scans everything.</summary>
    public ScanArea? Area { get; init; }

    /// <summary>-100..100, 0 is the device default.</summary>
    public int Brightness { get; init; }

    /// <summary>-100..100, 0 is the device default.</summary>
    public int Contrast { get; init; }

    /// <summary>True for a low-resolution preview of the whole bed.</summary>
    public bool IsPreview { get; init; }
}
