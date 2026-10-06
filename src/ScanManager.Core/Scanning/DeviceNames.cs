namespace ScanManager.Core.Scanning;

public static class DeviceNames
{
    /// <summary>
    /// Windows sometimes builds network scanner names by appending the same suffix
    /// several times, e.g. "Canon MF741C (d5:97:d9) (2) (d5:97:d9) (d5 (d5:97:d9) (2)".
    /// This keeps the name up to the first repetition of its first parenthesised group.
    /// </summary>
    public static string Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Unknown scanner";
        name = name.Trim();

        int open = name.IndexOf('(');
        if (open < 0) return name;
        int close = name.IndexOf(')', open);
        if (close < 0) return name;

        string group = name[open..(close + 1)];
        int repeat = name.IndexOf(group, close + 1, StringComparison.Ordinal);
        return repeat < 0 ? name : name[..repeat].TrimEnd();
    }
}
