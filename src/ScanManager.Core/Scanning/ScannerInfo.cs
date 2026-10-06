namespace ScanManager.Core.Scanning;

/// <summary>A scanner as reported by one of the scanner backends.</summary>
/// <param name="Id">Backend-specific device id, stable across sessions.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="BackendId">Id of the <see cref="IScannerBackend"/> that owns the device.</param>
public sealed record ScannerInfo(string Id, string Name, string BackendId)
{
    public override string ToString() => Name;
}
