namespace ScanManager.Core.Scanning;

/// <summary>A scanning technology (WIA, TWAIN, eSCL, a simulator, ...).</summary>
public interface IScannerBackend
{
    string BackendId { get; }

    Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans one image (flatbed) or every page in the feeder. Cancellation is honoured
    /// between pages; a page that is already being transferred is completed first.
    /// </summary>
    IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings, CancellationToken cancellationToken = default);
}
