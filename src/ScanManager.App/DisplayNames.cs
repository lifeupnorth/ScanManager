using ScanManager.Core.Scanning;

namespace ScanManager.App;

/// <summary>User-facing names of scan options, shared by dropdowns, dialogs and status messages.</summary>
public static class DisplayNames
{
    public static string For(ScanSource source) => source switch
    {
        ScanSource.Flatbed => "Flatbed (glass)",
        ScanSource.Feeder => "Document feeder",
        ScanSource.Duplex => "Document feeder, both sides",
        _ => source.ToString(),
    };

    public static string For(ColorMode mode) => mode switch
    {
        ColorMode.Color => "Color",
        ColorMode.Grayscale => "Grayscale",
        ColorMode.BlackAndWhite => "Black & white",
        _ => mode.ToString(),
    };
}
