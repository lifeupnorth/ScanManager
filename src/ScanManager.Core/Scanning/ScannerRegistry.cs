namespace ScanManager.Core.Scanning;

/// <summary>
/// Combines the devices of all scanner backends into one list. Backends are asked in
/// order and the first one to report a device id wins, so a network scanner reachable
/// over eSCL isn't listed a second time through WIA.
/// </summary>
public sealed class ScannerRegistry(IReadOnlyList<IScannerBackend> backends) : IDisposable
{
    public async Task<(IReadOnlyList<ScannerInfo> Devices, IReadOnlyList<string> Errors)> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<ScannerInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var backend in backends)
        {
            try
            {
                foreach (var device in await backend.GetDevicesAsync(ct))
                {
                    if (seen.Add(device.Id)) devices.Add(device);
                }
            }
            catch (ScannerException ex)
            {
                errors.Add(ex.Message);
            }
        }
        return (devices, errors);
    }

    public IScannerBackend BackendFor(ScannerInfo device) =>
        backends.FirstOrDefault(b => b.BackendId == device.BackendId)
        ?? throw new ScannerException($"No backend handles '{device.Name}'.", ScannerErrorKind.NotFound);

    public void Dispose()
    {
        foreach (var backend in backends.OfType<IDisposable>())
            backend.Dispose();
    }
}
