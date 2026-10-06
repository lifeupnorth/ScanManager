namespace ScanManager.Core.Scanning;

public enum ScannerErrorKind
{
    General,
    FeederEmpty,
    PaperJam,
    PaperProblem,
    Busy,
    Offline,
    CoverOpen,
    WarmingUp,
    NotFound,
    ServiceUnavailable,
}

/// <summary>A scanner failure with a message suitable for showing to the user.</summary>
public sealed class ScannerException(string message, ScannerErrorKind kind = ScannerErrorKind.General, Exception? inner = null)
    : Exception(message, inner)
{
    public ScannerErrorKind Kind { get; } = kind;
}
