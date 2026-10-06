namespace ScanManager.Core.Scanning;

/// <summary>Where the paper is fed from.</summary>
public enum ScanSource
{
    Flatbed,
    Feeder,
    /// <summary>Automatic document feeder, both sides of every sheet.</summary>
    Duplex,
}

public enum ColorMode
{
    Color,
    Grayscale,
    BlackAndWhite,
}
