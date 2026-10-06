using System.Globalization;

namespace ScanManager.Core.Geometry;

/// <summary>A rectangle in inches, measured from the top-left corner of the bed or page.</summary>
public readonly record struct ScanArea(double X, double Y, double Width, double Height)
{
    /// <summary>Smallest area the app lets the user select (about 6 mm).</summary>
    public const double MinimumSize = 0.25;

    public static ScanArea Full(SizeInches size) => new(0, 0, size.Width, size.Height);

    public SizeInches Size => new(Width, Height);
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Shrinks and moves the area so it lies within <paramref name="bounds"/>.</summary>
    public ScanArea ClampTo(SizeInches bounds)
    {
        double w = Math.Clamp(Width, Math.Min(MinimumSize, bounds.Width), bounds.Width);
        double h = Math.Clamp(Height, Math.Min(MinimumSize, bounds.Height), bounds.Height);
        double x = Math.Clamp(X, 0, bounds.Width - w);
        double y = Math.Clamp(Y, 0, bounds.Height - h);
        return new ScanArea(x, y, w, h);
    }

    /// <summary>Converts to fractions (0..1) of <paramref name="bounds"/>.</summary>
    public (double X, double Y, double Width, double Height) ToNormalized(SizeInches bounds) =>
        bounds.IsEmpty
            ? (0, 0, 1, 1)
            : (X / bounds.Width, Y / bounds.Height, Width / bounds.Width, Height / bounds.Height);

    public static ScanArea FromNormalized(double x, double y, double width, double height, SizeInches bounds) =>
        new(x * bounds.Width, y * bounds.Height, width * bounds.Width, height * bounds.Height);

    public PixelRect ToPixels(int dpi) => new(
        (int)Math.Round(X * dpi),
        (int)Math.Round(Y * dpi),
        Math.Max(1, (int)Math.Round(Width * dpi)),
        Math.Max(1, (int)Math.Round(Height * dpi)));

    public bool ApproximatelyEquals(ScanArea other, double tolerance = 0.01) =>
        Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance &&
        Math.Abs(Width - other.Width) <= tolerance && Math.Abs(Height - other.Height) <= tolerance;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Width:0.##} x {Height:0.##} in at ({X:0.##}, {Y:0.##})");
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height);
