namespace ScanManager.Core.Geometry;

/// <summary>A named paper size used as a scan-area preset.</summary>
public sealed record PaperSize(string Name, SizeInches Size)
{
    public static readonly PaperSize Letter = new("Letter (8.5 × 11 in)", new(8.5, 11));
    public static readonly PaperSize Legal = new("Legal (8.5 × 14 in)", new(8.5, 14));
    public static readonly PaperSize Executive = new("Executive (7.25 × 10.5 in)", new(7.25, 10.5));
    public static readonly PaperSize A4 = new("A4 (210 × 297 mm)", SizeInches.FromMillimeters(210, 297));
    public static readonly PaperSize A5 = new("A5 (148 × 210 mm)", SizeInches.FromMillimeters(148, 210));
    public static readonly PaperSize A6 = new("A6 (105 × 148 mm)", SizeInches.FromMillimeters(105, 148));
    public static readonly PaperSize B5 = new("B5 (176 × 250 mm)", SizeInches.FromMillimeters(176, 250));
    public static readonly PaperSize Photo4x6 = new("Photo (4 × 6 in)", new(4, 6));
    public static readonly PaperSize Photo5x7 = new("Photo (5 × 7 in)", new(5, 7));
    public static readonly PaperSize BusinessCard = new("Business card (3.5 × 2 in)", new(3.5, 2));

    public static IReadOnlyList<PaperSize> Standard { get; } =
        [Letter, Legal, Executive, A4, A5, A6, B5, Photo4x6, Photo5x7, BusinessCard];

    public override string ToString() => Name;
}
