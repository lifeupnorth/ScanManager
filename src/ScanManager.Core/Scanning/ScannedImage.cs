namespace ScanManager.Core.Scanning;

/// <summary>
/// One image delivered by a scanner, still in the device's encoded format (BMP, PNG, ...).
/// The flags report which requested adjustments the device performed itself, so the
/// app knows what is left to do in software.
/// </summary>
public sealed record ScannedImage(
    byte[] Data,
    int Dpi,
    int PageNumber,
    ColorMode DeliveredColorMode,
    bool BrightnessApplied,
    bool ContrastApplied);
