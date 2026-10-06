using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Core.Threading;
using static ScanManager.Wia.WiaConstants;

namespace ScanManager.Wia;

/// <summary>
/// Scanner access through Windows Image Acquisition (WIA 2.0 Automation Library).
/// Covers USB scanners with WIA drivers and network scanners that Windows exposes
/// through its built-in eSCL/WSD drivers. All COM calls run on one STA thread.
/// </summary>
public sealed class WiaScannerBackend : IScannerBackend, IDisposable
{
    public const string Id = "wia";

    private static readonly int[] StandardResolutions = [75, 100, 150, 200, 240, 300, 400, 600, 1200, 2400, 4800];

    private readonly StaWorker _sta = new("WIA worker");

    // Connected WIA.Device objects by device id. Only touched on the STA thread.
    private readonly Dictionary<string, object> _devices = new();

    public string BackendId => Id;

    public Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        _sta.InvokeAsync<IReadOnlyList<ScannerInfo>>(() => Guard(null, () =>
        {
            dynamic manager = CreateDeviceManager();
            var result = new List<ScannerInfo>();
            foreach (dynamic info in manager.DeviceInfos)
            {
                if ((int)info.Type != ScannerDeviceType) continue;
                object props = info.Properties;
                string id = info.DeviceID;
                string? name = WiaProperties.GetString(props, DipDevName) ?? WiaProperties.GetString(props, DipDevDesc);
                result.Add(new ScannerInfo(id, DeviceNames.Clean(name ?? id), Id));
            }
            return result;
        }));

    public Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default) =>
        _sta.InvokeAsync(() => Guard(deviceId, () => ReadCapabilities(deviceId)));

    public async IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ScannedImage>();
        var work = _sta.InvokeAsync(() =>
        {
            try
            {
                Guard(deviceId, () => { Scan(deviceId, settings, channel.Writer, cancellationToken); return 0; });
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        });

        await foreach (var image in channel.Reader.ReadAllAsync(cancellationToken))
            yield return image;
        await work;
    }

    public void Dispose() => _sta.Dispose();

    // ---- Capabilities -------------------------------------------------------------------

    private ScannerCapabilities ReadCapabilities(string deviceId)
    {
        object device = Connect(deviceId);
        object rootProps = ((dynamic)device).Properties;

        var sources = new List<ScanSource>();
        int? handling = WiaProperties.GetInt(rootProps, DpsDocumentHandlingCapabilities);
        bool canSelect = WiaProperties.Find(rootProps, DpsDocumentHandlingSelect) is not null;
        if (handling is int caps && canSelect)
        {
            if ((caps & Flatbed) != 0) sources.Add(ScanSource.Flatbed);
            if ((caps & Feeder) != 0) sources.Add(ScanSource.Feeder);
            if ((caps & Feeder) != 0 && (caps & Duplex) != 0) sources.Add(ScanSource.Duplex);
        }
        if (sources.Count == 0) sources.Add(ScanSource.Flatbed);

        int? originalSelection = WiaProperties.GetInt(rootProps, DpsDocumentHandlingSelect);
        var result = new List<SourceCapabilities>();
        try
        {
            foreach (var source in sources)
            {
                // Valid resolutions and sizes can differ per source, so switch to it before reading.
                if (canSelect)
                    WiaProperties.TrySetExact(rootProps, DpsDocumentHandlingSelect, HandlingFlags(source));
                result.Add(ReadSourceCapabilities(device, source));
            }
        }
        finally
        {
            if (canSelect && originalSelection is int original)
                WiaProperties.TrySetExact(Properties(device), DpsDocumentHandlingSelect, original);
        }
        return new ScannerCapabilities(result);
    }

    private static SourceCapabilities ReadSourceCapabilities(object device, ScanSource source)
    {
        object rootProps = Properties(device);
        object itemProps = Properties(FirstItem(device));

        var colorModes = new List<ColorMode>();
        if (WiaProperties.GetValidValues(WiaProperties.Find(itemProps, IpaDataType)) is { } dataTypes)
        {
            if (dataTypes.Contains(DataTypeColor)) colorModes.Add(ColorMode.Color);
            if (dataTypes.Contains(DataTypeGrayscale)) colorModes.Add(ColorMode.Grayscale);
            if (dataTypes.Contains(DataTypeThreshold)) colorModes.Add(ColorMode.BlackAndWhite);
        }
        if (colorModes.Count == 0) colorModes.AddRange([ColorMode.Color, ColorMode.Grayscale]);

        return new SourceCapabilities(
            source,
            ReadMaxSize(rootProps, itemProps, source != ScanSource.Flatbed),
            ReadResolutions(itemProps),
            colorModes,
            ReadAdjustableRange(itemProps, IpsBrightness),
            ReadAdjustableRange(itemProps, IpsContrast));
    }

    private static List<int> ReadResolutions(object itemProps)
    {
        object? res = WiaProperties.Find(itemProps, IpsXRes);
        if (WiaProperties.GetValidValues(res) is { Count: > 0 } list)
            return list.Distinct().Order().ToList();

        if (WiaProperties.GetRange(res) is var (min, max, step))
        {
            var values = StandardResolutions
                .Where(r => r >= min && r <= max && (step <= 1 || (r - min) % step == 0))
                .ToList();
            if (values.Count == 0) values.AddRange([min, max]);
            return values.Distinct().Order().ToList();
        }

        return WiaProperties.GetInt(itemProps, IpsXRes) is int current ? [current] : [300];
    }

    private static ValueRange? ReadAdjustableRange(object itemProps, int propertyId)
    {
        if (WiaProperties.GetRange(WiaProperties.Find(itemProps, propertyId)) is var (min, max, _))
        {
            var range = new ValueRange(min, max);
            return range.IsAdjustable ? range : null;
        }
        return null;
    }

    private static SizeInches ReadMaxSize(object rootProps, object itemProps, bool feeder)
    {
        int? w = WiaProperties.GetInt(rootProps, feeder ? DpsHorizontalSheetFeedSize : DpsHorizontalBedSize);
        int? h = WiaProperties.GetInt(rootProps, feeder ? DpsVerticalSheetFeedSize : DpsVerticalBedSize);
        if (w > 0 && h > 0) return new SizeInches(w.Value / 1000.0, h.Value / 1000.0);

        // Fall back to the extent range at the current resolution.
        int dpi = WiaProperties.GetInt(itemProps, IpsXRes) ?? 0;
        var xr = WiaProperties.GetRange(WiaProperties.Find(itemProps, IpsXExtent));
        var yr = WiaProperties.GetRange(WiaProperties.Find(itemProps, IpsYExtent));
        if (dpi > 0 && xr is not null && yr is not null)
            return new SizeInches((double)xr.Value.Max / dpi, (double)yr.Value.Max / dpi);

        // Feeders with "unlimited" length often report 0; legal is a safe default.
        return new SizeInches(w > 0 ? w.Value / 1000.0 : 8.5, feeder ? 14 : 11.69);
    }

    // ---- Scanning -----------------------------------------------------------------------

    private void Scan(string deviceId, ScanSettings settings, ChannelWriter<ScannedImage> output, CancellationToken ct)
    {
        object device = Connect(deviceId);
        object rootProps = Properties(device);
        bool fromFeeder = settings.Source != ScanSource.Flatbed;

        if (WiaProperties.Find(rootProps, DpsDocumentHandlingSelect) is not null)
        {
            if (!WiaProperties.TrySetExact(rootProps, DpsDocumentHandlingSelect, HandlingFlags(settings.Source)) && fromFeeder)
                throw new ScannerException(settings.Source == ScanSource.Duplex
                    ? "This scanner can't scan both sides of the page."
                    : "This scanner has no document feeder.");
        }
        else if (fromFeeder)
        {
            throw new ScannerException("This scanner has no document feeder.");
        }

        // One page per transfer; the loop below keeps transferring until the feeder is empty.
        if (fromFeeder) WiaProperties.TrySet(rootProps, DpsPages, 1);
        WiaProperties.TrySetExact(rootProps, DpsPreview, settings.IsPreview ? 1 : 0);

        object item = FirstItem(device);
        ColorMode delivered = ApplyColorMode(Properties(item), settings.ColorMode);

        object props = Properties(item);
        WiaProperties.TrySet(props, IpsXRes, settings.Dpi);
        WiaProperties.TrySet(props, IpsYRes, settings.Dpi);
        props = Properties(item);
        int dpi = WiaProperties.GetInt(props, IpsXRes) ?? settings.Dpi;

        bool brightnessApplied = ApplyAdjustment(props, IpsBrightness, settings.Brightness);
        bool contrastApplied = ApplyAdjustment(props, IpsContrast, settings.Contrast);

        ApplyArea(rootProps, item, settings, dpi, fromFeeder);

        int page = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            object imageFile;
            try
            {
                imageFile = Transfer(item);
            }
            catch (COMException ex) when (fromFeeder && ex.HResult == ErrorPaperEmpty)
            {
                if (page == 0)
                    throw new ScannerException("The document feeder is empty. Load the pages and try again.", ScannerErrorKind.FeederEmpty, ex);
                break;
            }

            byte[] data = (byte[])((dynamic)imageFile).FileData.BinaryData;
            page++;
            output.TryWrite(new ScannedImage(data, dpi, page, delivered, brightnessApplied, contrastApplied));

            if (!fromFeeder) break;
            if (settings.MaxPages > 0 && page >= settings.MaxPages) break;
        }
    }

    private static ColorMode ApplyColorMode(object props, ColorMode requested)
    {
        // Intent first: on many drivers it resets data type, depth and resolution.
        int intent = requested switch
        {
            ColorMode.Color => IntentColor,
            ColorMode.Grayscale => IntentGrayscale,
            _ => IntentText,
        };
        if (!WiaProperties.TrySetExact(props, IpsCurIntent, intent) && requested == ColorMode.BlackAndWhite)
            WiaProperties.TrySetExact(props, IpsCurIntent, IntentGrayscale);

        var dataTypes = WiaProperties.GetValidValues(WiaProperties.Find(props, IpaDataType));
        bool Has(int t) => dataTypes is null || dataTypes.Contains(t);

        if (requested == ColorMode.BlackAndWhite && dataTypes?.Contains(DataTypeThreshold) == true
            && WiaProperties.TrySetExact(props, IpaDataType, DataTypeThreshold))
        {
            WiaProperties.TrySetExact(props, IpaDepth, 1);
            return ColorMode.BlackAndWhite;
        }

        // Black & white falls back to grayscale; the app thresholds it afterwards.
        if (requested != ColorMode.Color && Has(DataTypeGrayscale)
            && WiaProperties.TrySetExact(props, IpaDataType, DataTypeGrayscale))
        {
            WiaProperties.TrySetExact(props, IpaDepth, 8);
            return ColorMode.Grayscale;
        }

        if (WiaProperties.TrySetExact(props, IpaDataType, DataTypeColor))
            WiaProperties.TrySetExact(props, IpaDepth, 24);
        return ColorMode.Color;
    }

    /// <summary>Applies a -100..100 adjustment. Returns true when the device handled it (or there was nothing to do).</summary>
    private static bool ApplyAdjustment(object props, int propertyId, int percent)
    {
        if (ReadAdjustableRange(props, propertyId) is not ValueRange range)
            return percent == 0;
        return WiaProperties.TrySet(props, propertyId, range.MapFromPercent(percent)) || percent == 0;
    }

    private static void ApplyArea(object rootProps, object item, ScanSettings settings, int dpi, bool fromFeeder)
    {
        object props = Properties(item);
        var max = ReadMaxSize(rootProps, props, fromFeeder);
        var area = (settings.Area ?? ScanArea.Full(max)).ClampTo(max);

        if (fromFeeder)
        {
            // In a feeder the page position is fixed by the paper guides, so only the
            // size matters; place it where the feeder aligns paper.
            double x = WiaProperties.GetInt(rootProps, DpsSheetFeederRegistration) switch
            {
                Centered => (max.Width - area.Width) / 2,
                RightJustified => max.Width - area.Width,
                _ => 0,
            };
            area = area with { X = x, Y = 0 };
        }

        var px = area.ToPixels(dpi);

        // Shrink the extents first so the start position is free to move, then grow them.
        WiaProperties.TrySet(props, IpsXExtent, 1);
        WiaProperties.TrySet(props, IpsYExtent, 1);
        WiaProperties.TrySet(Properties(item), IpsXPos, px.X);
        WiaProperties.TrySet(Properties(item), IpsYPos, px.Y);
        WiaProperties.TrySet(Properties(item), IpsXExtent, px.Width);
        WiaProperties.TrySet(Properties(item), IpsYExtent, px.Height);
    }

    private static object Transfer(object item)
    {
        try
        {
            return ((dynamic)item).Transfer(FormatBmp);
        }
        catch (COMException ex) when ((uint)ex.HResult >> 16 != 0x8021)
        {
            // Not a scanner error: most likely the driver doesn't offer BMP. Use its default format.
            return ((dynamic)item).Transfer();
        }
    }

    // ---- Plumbing -----------------------------------------------------------------------

    private static int HandlingFlags(ScanSource source) => source switch
    {
        ScanSource.Feeder => Feeder,
        ScanSource.Duplex => Feeder | Duplex,
        _ => Flatbed,
    };

    private static object Properties(object wiaObject) => ((dynamic)wiaObject).Properties;

    private static object FirstItem(object device)
    {
        foreach (object item in ((dynamic)device).Items)
            return item;
        throw new ScannerException("The scanner did not report any scan items.");
    }

    private static dynamic CreateDeviceManager()
    {
        var type = Type.GetTypeFromProgID("WIA.DeviceManager")
            ?? throw new ScannerException("Windows Image Acquisition is not available on this computer.", ScannerErrorKind.ServiceUnavailable);
        return Activator.CreateInstance(type)!;
    }

    private object Connect(string deviceId)
    {
        if (_devices.TryGetValue(deviceId, out var cached)) return cached;

        dynamic manager = CreateDeviceManager();
        foreach (dynamic info in manager.DeviceInfos)
        {
            if ((string)info.DeviceID != deviceId) continue;
            object device = info.Connect();
            _devices[deviceId] = device;
            return device;
        }
        throw new ScannerException("The scanner is not available. Check that it is turned on and connected.", ScannerErrorKind.NotFound);
    }

    /// <summary>Runs <paramref name="action"/>, turning COM failures into user-readable <see cref="ScannerException"/>s.</summary>
    private T Guard<T>(string? deviceId, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (COMException ex)
        {
            // The connection may be stale (scanner restarted, network dropped): reconnect next time.
            if (deviceId is not null) _devices.Remove(deviceId);
            throw Translate(ex);
        }
    }

    internal static ScannerException Translate(COMException ex) => ex.HResult switch
    {
        ErrorPaperJam => new("Paper jam. Clear the jammed paper and try again.", ScannerErrorKind.PaperJam, ex),
        ErrorPaperEmpty => new("The document feeder is empty. Load the pages and try again.", ScannerErrorKind.FeederEmpty, ex),
        ErrorPaperProblem => new("There is a problem with the paper in the document feeder.", ScannerErrorKind.PaperProblem, ex),
        ErrorOffline or ErrorNoDeviceAvailable or ErrorDeviceCommunication or ErrorItemDeleted =>
            new("The scanner is offline or can't be reached. Check that it is turned on and connected.", ScannerErrorKind.Offline, ex),
        ErrorBusy or ErrorDeviceLocked => new("The scanner is busy. Wait for it to finish and try again.", ScannerErrorKind.Busy, ex),
        ErrorWarmingUp => new("The scanner is warming up. Try again in a moment.", ScannerErrorKind.WarmingUp, ex),
        ErrorCoverOpen => new("The scanner cover is open. Close it and try again.", ScannerErrorKind.CoverOpen, ex),
        ErrorLampOff => new("The scanner lamp is off.", ScannerErrorKind.General, ex),
        ErrorUserIntervention => new("The scanner needs attention. Check its display or control panel.", ScannerErrorKind.General, ex),
        ErrorIncorrectHardwareSetting or ErrorInvalidCommand =>
            new("The scanner rejected these settings. Try a different source, resolution or scan area.", ScannerErrorKind.General, ex),
        _ => new($"The scanner reported an error (0x{ex.HResult:X8}). {ex.Message}", ScannerErrorKind.General, ex),
    };
}
