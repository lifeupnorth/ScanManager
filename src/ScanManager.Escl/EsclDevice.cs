namespace ScanManager.Escl;

/// <summary>A network scanner that speaks eSCL (AirScan / Mopria scan).</summary>
/// <param name="Id">Stable id; on Windows this is the device's PnP instance id, which WIA uses too.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="BaseUri">The eSCL root, e.g. http://192.168.0.7/eSCL/.</param>
public sealed record EsclDevice(string Id, string Name, Uri BaseUri);

/// <summary>Finds eSCL scanners (from the OS device list, mDNS, manual entry, ...).</summary>
public interface IEsclDeviceSource
{
    Task<IReadOnlyList<EsclDevice>> FindDevicesAsync(CancellationToken cancellationToken = default);
}
