namespace ScanManager.Core.Scanning;

public static class SourcePreference
{
    /// <summary>
    /// Picks the source to select for a scanner offering <paramref name="available"/>:
    /// <paramref name="preferred"/> when given (the user's default), otherwise
    /// <paramref name="current"/>. If the scanner lacks it, the closest it has is used:
    /// both sides falls back to one side, and the feeder falls back to the glass.
    /// </summary>
    public static ScanSource Choose(IReadOnlyList<ScanSource> available, ScanSource? preferred, ScanSource current)
    {
        if (available.Count == 0) return ScanSource.Flatbed;
        foreach (var candidate in FallbackOrder(preferred ?? current))
        {
            if (available.Contains(candidate)) return candidate;
        }
        return available[0];
    }

    private static ScanSource[] FallbackOrder(ScanSource source) => source switch
    {
        ScanSource.Duplex => [ScanSource.Duplex, ScanSource.Feeder, ScanSource.Flatbed],
        ScanSource.Feeder => [ScanSource.Feeder, ScanSource.Flatbed],
        _ => [ScanSource.Flatbed],
    };
}
