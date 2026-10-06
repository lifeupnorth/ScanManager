using ScanManager.Core.Scanning;

namespace ScanManager.App.Services;

/// <summary>Combines the devices of all scanner backends into one list.</summary>
public sealed class ScannerRegistry(IReadOnlyList<IScannerBackend> backends) : IDisposable
{
    public async Task<(IReadOnlyList<ScannerInfo> Devices, IReadOnlyList<string> Errors)> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = new List<ScannerInfo>();
        var errors = new List<string>();
        foreach (var backend in backends)
        {
            try
            {
                devices.AddRange(await backend.GetDevicesAsync(ct));
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
