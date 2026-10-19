namespace ScanManager.Core.Scanning;

/// <summary>A scanning technology (WIA, TWAIN, eSCL, a simulator, ...).</summary>
public interface IScannerBackend
{
    string BackendId { get; }

    Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);

    Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans one image (flatbed) or every page in the feeder. Cancellation stops the job
    /// as soon as the backend can; a page that is still being transferred is dropped.
    /// </summary>
    IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings, CancellationToken cancellationToken = default);
}
