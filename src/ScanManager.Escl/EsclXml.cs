using System.Globalization;
using System.Xml.Linq;

namespace ScanManager.Escl;

/// <summary>Capabilities of one input (platen, feeder simplex, feeder duplex). Sizes in 1/300 inch.</summary>
public sealed record EsclInputCaps(
    int MaxWidth,
    int MaxHeight,
    IReadOnlyList<int> Resolutions,
    IReadOnlyList<string> ColorModes,
    IReadOnlyList<string> DocumentFormats);

public sealed record EsclRange(int Min, int Max);

public sealed record EsclCapabilities(
    string Version,
    EsclInputCaps? Platen,
    EsclInputCaps? AdfSimplex,
    EsclInputCaps? AdfDuplex,
    EsclRange? Brightness,
    EsclRange? Contrast);

public sealed record EsclStatus(string State, string? AdfState);

/// <summary>The settings of one eSCL scan job. Region in 1/300 inch.</summary>
public sealed record EsclScanRequest(
    string Version,
    string InputSource,   // "Platen" or "Feeder"
    bool Duplex,
    string ColorMode,     // "RGB24", "Grayscale8", "BlackAndWhite1"
    int Resolution,
    int XOffset,
    int YOffset,
    int Width,
    int Height,
    string DocumentFormat,
    int? Brightness,
    int? Contrast);

/// <summary>Reads and writes the eSCL XML documents (Mopria eSCL specification).</summary>
internal static class EsclXml
{
    public static readonly XNamespace Scan = "http://schemas.hp.com/imaging/escl/2011/05/03";
    public static readonly XNamespace Pwg = "http://www.pwg.org/schemas/2010/12/sm";

    private static readonly int[] StandardResolutions = [75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400];

    public static EsclCapabilities ParseCapabilities(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("Empty scanner capabilities.");
        return new EsclCapabilities(
            root.Element(Pwg + "Version")?.Value ?? "2.0",
            ParseInput(root.Element(Scan + "Platen")?.Element(Scan + "PlatenInputCaps")),
            ParseInput(root.Element(Scan + "Adf")?.Element(Scan + "AdfSimplexInputCaps")),
            ParseInput(root.Element(Scan + "Adf")?.Element(Scan + "AdfDuplexInputCaps")),
            ParseRange(root.Element(Scan + "BrightnessSupport")),
            ParseRange(root.Element(Scan + "ContrastSupport")));
    }

    private static EsclInputCaps? ParseInput(XElement? caps)
    {
        if (caps is null) return null;

        var resolutions = caps.Descendants(Scan + "DiscreteResolution")
            .Select(r => Int(r.Element(Scan + "XResolution")))
            .Where(r => r > 0)
            .ToList();

        foreach (var range in caps.Descendants(Scan + "XResolutionRange"))
        {
            int min = Int(range.Element(Scan + "Min")), max = Int(range.Element(Scan + "Max"));
            int step = Math.Max(1, Int(range.Element(Scan + "Step")));
            resolutions.AddRange(StandardResolutions.Where(r => r >= min && r <= max && (r - min) % step == 0));
        }

        return new EsclInputCaps(
            Int(caps.Element(Scan + "MaxWidth")),
            Int(caps.Element(Scan + "MaxHeight")),
            resolutions.Distinct().Order().ToList(),
            caps.Descendants(Scan + "ColorMode").Select(e => e.Value.Trim()).Distinct().ToList(),
            caps.Descendants(Pwg + "DocumentFormat").Concat(caps.Descendants(Scan + "DocumentFormatExt"))
                .Select(e => e.Value.Trim()).Distinct().ToList());
    }

    private static EsclRange? ParseRange(XElement? element)
    {
        if (element is null) return null;
        int min = Int(element.Element(Scan + "Min")), max = Int(element.Element(Scan + "Max"));
        return max > min ? new EsclRange(min, max) : null;
    }

    public static EsclStatus ParseStatus(string xml)
    {
        var root = XDocument.Parse(xml).Root ?? throw new FormatException("Empty scanner status.");
        return new EsclStatus(
            root.Element(Pwg + "State")?.Value.Trim() ?? "Unknown",
            root.Element(Scan + "AdfState")?.Value.Trim());
    }

    public static string BuildScanSettings(EsclScanRequest r)
    {
        var settings = new XElement(Scan + "ScanSettings",
            new XAttribute(XNamespace.Xmlns + "scan", Scan),
            new XAttribute(XNamespace.Xmlns + "pwg", Pwg),
            new XElement(Pwg + "Version", r.Version),
            new XElement(Pwg + "ScanRegions",
                new XElement(Pwg + "ScanRegion",
                    new XElement(Pwg + "ContentRegionUnits", "escl:ThreeHundredthsOfInches"),
                    new XElement(Pwg + "XOffset", r.XOffset),
                    new XElement(Pwg + "YOffset", r.YOffset),
                    new XElement(Pwg + "Width", r.Width),
                    new XElement(Pwg + "Height", r.Height))),
            new XElement(Pwg + "InputSource", r.InputSource),
            new XElement(Scan + "ColorMode", r.ColorMode),
            new XElement(Scan + "XResolution", r.Resolution),
            new XElement(Scan + "YResolution", r.Resolution),
            new XElement(Pwg + "DocumentFormat", r.DocumentFormat));

        // DocumentFormatExt arrived in eSCL 2.1; older devices may reject unknown elements.
        if (VersionAtLeast(r.Version, 2, 1))
            settings.Add(new XElement(Scan + "DocumentFormatExt", r.DocumentFormat));
        if (r.InputSource == "Feeder")
            settings.Add(new XElement(Scan + "Duplex", r.Duplex ? "true" : "false"));
        if (r.Brightness is int brightness)
            settings.Add(new XElement(Scan + "Brightness", brightness));
        if (r.Contrast is int contrast)
            settings.Add(new XElement(Scan + "Contrast", contrast));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), settings).Declaration + "\n" + settings;
    }

    internal static bool VersionAtLeast(string version, int major, int minor)
    {
        var parts = version.Split('.');
        int ma = parts.Length > 0 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int a) ? a : 0;
        int mi = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int b) ? b : 0;
        return ma > major || ma == major && mi >= minor;
    }

    private static int Int(XElement? element) =>
        element is not null && int.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
}
