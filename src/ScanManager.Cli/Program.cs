// scancli: command-line access to the scanning layer. Handy for diagnosing driver
// problems without the UI, and for scripted scanning.
//
//   scancli list
//   scancli caps [device#]
//   scancli scan [device#] [--source flatbed|feeder|duplex] [--dpi N] [--color color|gray|bw]
//                [--area x,y,w,h] [--preview] [--out file]

using System.Globalization;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Escl;
using ScanManager.Wia;

using var registry = new ScannerRegistry([new EsclScannerBackend(new WindowsEsclDeviceSource()), new WiaScannerBackend()]);

try
{
    string command = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
    var (devices, _) = await registry.GetDevicesAsync();

    switch (command)
    {
        case "list":
            if (devices.Count == 0) Console.WriteLine("No WIA scanners found.");
            for (int i = 0; i < devices.Count; i++)
                Console.WriteLine($"{i + 1}. {devices[i].Name} [{devices[i].BackendId}]\n   {devices[i].Id}");
            return 0;

        case "caps":
        {
            var device = PickDevice(devices, args);
            var caps = await registry.BackendFor(device).GetCapabilitiesAsync(device.Id);
            Console.WriteLine(device.Name);
            foreach (var s in caps.Sources)
            {
                Console.WriteLine($"  {s.Source}: max {s.MaxSize}, dpi [{string.Join(", ", s.Resolutions)}], " +
                                  $"modes [{string.Join(", ", s.NativeColorModes)}], " +
                                  $"brightness {s.Brightness?.ToString() ?? "n/a"}, contrast {s.Contrast?.ToString() ?? "n/a"}");
            }
            return 0;
        }

        case "scan":
        {
            var device = PickDevice(devices, args);
            var settings = new ScanSettings
            {
                Source = Option("--source")?.ToLowerInvariant() switch
                {
                    "feeder" => ScanSource.Feeder,
                    "duplex" => ScanSource.Duplex,
                    _ => ScanSource.Flatbed,
                },
                ColorMode = Option("--color")?.ToLowerInvariant() switch
                {
                    "gray" or "grayscale" => ColorMode.Grayscale,
                    "bw" => ColorMode.BlackAndWhite,
                    _ => ColorMode.Color,
                },
                Dpi = int.TryParse(Option("--dpi"), out int dpi) ? dpi : 300,
                Area = ParseArea(Option("--area")),
                IsPreview = args.Contains("--preview"),
            };
            string output = Option("--out") ?? "scan.bmp";

            Console.WriteLine($"Scanning from {device.Name} ({settings.Source}, {settings.Dpi} dpi, {settings.ColorMode})...");
            int count = 0;
            await foreach (var image in registry.BackendFor(device).ScanAsync(device.Id, settings))
            {
                count++;
                string path = count == 1 ? output : NumberedPath(output, count);
                await File.WriteAllBytesAsync(path, image.Data);
                Console.WriteLine($"  page {image.PageNumber}: {image.Data.Length:N0} bytes, {image.Dpi} dpi, {image.DeliveredColorMode} -> {path}");
            }
            if (count > 1)
                File.Move(output, NumberedPath(output, 1), overwrite: true);
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'. Use list, caps or scan.");
            return 2;
    }
}
catch (ScannerException ex)
{
    Console.Error.WriteLine($"Scanner error ({ex.Kind}): {ex.Message}");
    if (ex.InnerException is not null)
        Console.Error.WriteLine($"\nDetails:\n{ex.InnerException}");
    return 1;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static ScannerInfo PickDevice(IReadOnlyList<ScannerInfo> devices, string[] args)
{
    if (devices.Count == 0) throw new ScannerException("No WIA scanners found.", ScannerErrorKind.NotFound);
    int index = args.Length > 1 && int.TryParse(args[1], out int n) ? n - 1 : 0;
    if (index < 0 || index >= devices.Count) throw new ScannerException($"There is no scanner #{index + 1}.", ScannerErrorKind.NotFound);
    return devices[index];
}

static ScanArea? ParseArea(string? text)
{
    if (text is null) return null;
    var parts = text.Split(',').Select(p => double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    return parts.Length == 4 ? new ScanArea(parts[0], parts[1], parts[2], parts[3]) : null;
}

static string NumberedPath(string path, int page) =>
    Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}_{page:000}{Path.GetExtension(path)}");
