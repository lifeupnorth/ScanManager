using System.Globalization;

namespace ScanManager.Core.Geometry;

public readonly record struct SizeInches(double Width, double Height)
{
    public const double MillimetersPerInch = 25.4;

    public static SizeInches FromMillimeters(double width, double height) =>
        new(width / MillimetersPerInch, height / MillimetersPerInch);

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Fits(SizeInches container, double tolerance = 0.02) =>
        Width <= container.Width + tolerance && Height <= container.Height + tolerance;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width:0.##} x {Height:0.##} in");
}
