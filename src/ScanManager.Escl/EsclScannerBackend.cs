using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;

namespace ScanManager.Escl;

/// <summary>
/// Network scanners through eSCL (the protocol behind AirScan and Mopria scanning), spoken
/// directly over HTTP. Going direct avoids the limits of the WIA layer on top of Windows'
/// eSCL class driver, which for example rejects duplex scans on some devices.
/// </summary>
public sealed class EsclScannerBackend : IScannerBackend, IDisposable
{
    public const string Id = "escl";

    /// <summary>
    /// How long a quick request (the discovery probe, a status check) waits for an answer. Discovery
    /// leaves a silent scanner to other backends; a status check just goes without the status.
    /// </summary>
    private static readonly TimeSpan QuickRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long Cancel waits for a page that is already being downloaded before abandoning it.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromSeconds(30);

    private readonly IEsclDeviceSource _source;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, EsclClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EsclCapabilities> _capabilities = new(StringComparer.OrdinalIgnoreCase);

    public EsclScannerBackend(IEsclDeviceSource source, HttpClient? http = null)
    {
        _source = source;
        _ownsHttp = http is null;
        // Fetching a page blocks while the scanner scans it, which can take a while at high resolution.
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
    }

    public string BackendId => Id;

    /// <summary>Lists eSCL scanners that answer; unreachable ones are left to other backends (WIA).</summary>
    public async Task<IReadOnlyList<ScannerInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _source.FindDevicesAsync(cancellationToken);
        var probes = devices.Select(async device =>
        {
            var client = new EsclClient(_http, device.BaseUri);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(QuickRequestTimeout);
                _capabilities[device.Id] = await client.GetCapabilitiesAsync(timeout.Token);
                _clients[device.Id] = client;
                return new ScannerInfo(device.Id, device.Name, Id);
            }
            catch (Exception ex) when (ex is HttpRequestException or EsclHttpException or FormatException or System.Xml.XmlException
                                           || IsTimeout(ex, cancellationToken))
            {
                return null;
            }
        });
        return (await Task.WhenAll(probes)).OfType<ScannerInfo>().ToList();
    }

    public async Task<ScannerCapabilities> GetCapabilitiesAsync(string deviceId, CancellationToken cancellationToken = default) =>
        ToScannerCapabilities(await GetEsclCapabilitiesAsync(deviceId, cancellationToken));

    public async IAsyncEnumerable<ScannedImage> ScanAsync(string deviceId, ScanSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = ClientFor(deviceId);
        var caps = await GetEsclCapabilitiesAsync(deviceId, cancellationToken);
        bool feeder = settings.Source != ScanSource.Flatbed;
        var input = settings.Source switch
        {
            ScanSource.Duplex => caps.AdfDuplex,
            ScanSource.Feeder => caps.AdfSimplex ?? caps.AdfDuplex,
            _ => caps.Platen,
        } ?? throw new ScannerException(settings.Source switch
        {
            ScanSource.Duplex => "This scanner can't scan both sides of the page.",
            ScanSource.Feeder => "This scanner has no document feeder.",
            _ => "This scanner has no flatbed.",
        });

        if (feeder)
            await EnsureFeederReadyAsync(client, cancellationToken);

        var (colorMode, delivered) = PickColorMode(input, settings.ColorMode);
        int dpi = input.Resolutions.Count > 0 ? input.Resolutions.MinBy(r => Math.Abs(r - settings.Dpi)) : settings.Dpi;
        var max = new SizeInches(input.MaxWidth / 300.0, input.MaxHeight / 300.0);
        var area = (settings.Area ?? ScanArea.Full(max)).ClampTo(max);
        if (feeder) area = area with { X = 0, Y = 0 };
        var region = area.ToPixels(300); // eSCL regions are always in 1/300 inch

        int? brightness = settings.Brightness != 0 && caps.Brightness is { } b ? MapPercent(b, settings.Brightness) : null;
        int? contrast = settings.Contrast != 0 && caps.Contrast is { } c ? MapPercent(c, settings.Contrast) : null;

        var request = new EsclScanRequest(
            caps.Version,
            feeder ? "Feeder" : "Platen",
            settings.Source == ScanSource.Duplex,
            colorMode,
            dpi,
            region.X, region.Y,
            Math.Min(region.Width, input.MaxWidth), Math.Min(region.Height, input.MaxHeight),
            PickFormat(input),
            brightness,
            contrast);

        Uri job;
        try
        {
            job = await client.CreateJobAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is EsclHttpException or HttpRequestException || IsTimeout(ex, cancellationToken))
        {
            throw await ExplainAsync(client, ex, feeder);
        }

        // Cancel takes effect between pages: a page already being downloaded is finished and kept,
        // since the scanner has fed that sheet anyway. A download still stuck after the grace is dropped.
        using var download = new CancellationTokenSource();
        using var onCancel = cancellationToken.Register(() => download.CancelAfter(CancelGrace));
        bool finished = false;
        try
        {
            for (int page = 1; ; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[]? data;
                try
                {
                    data = await client.NextDocumentAsync(job, download.Token);
                }
                catch (Exception ex) when (ex is EsclHttpException or HttpRequestException || IsTimeout(ex, cancellationToken))
                {
                    throw await ExplainAsync(client, ex, feeder);
                }

                if (data is null)
                {
                    finished = true;
                    if (page == 1)
                        throw new ScannerException(feeder
                            ? "The document feeder is empty. Load the pages and try again."
                            : "The scanner finished without returning an image.",
                            feeder ? ScannerErrorKind.FeederEmpty : ScannerErrorKind.General);
                    yield break;
                }

                yield return new ScannedImage(data, dpi, page, delivered, brightness is not null || settings.Brightness == 0,
                    contrast is not null || settings.Contrast == 0);

                if (!feeder)
                    yield break;
            }
        }
        finally
        {
            // Stop the scanner when we leave early (flatbed done, cancel, error).
            if (!finished)
                await client.CancelJobAsync(job);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // ---- Helpers ------------------------------------------------------------------------

    private EsclClient ClientFor(string deviceId) =>
        _clients.TryGetValue(deviceId, out var client)
            ? client
            : throw new ScannerException("The scanner is not available. Click Refresh to look for it again.", ScannerErrorKind.NotFound);

    private async Task<EsclCapabilities> GetEsclCapabilitiesAsync(string deviceId, CancellationToken ct)
    {
        if (_capabilities.TryGetValue(deviceId, out var cached)) return cached;
        var client = ClientFor(deviceId);
        try
        {
            return _capabilities[deviceId] = await client.GetCapabilitiesAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or EsclHttpException)
        {
            throw Unreachable(client, ex);
        }
    }

    internal static ScannerCapabilities ToScannerCapabilities(EsclCapabilities caps)
    {
        var sources = new List<SourceCapabilities>();
        void Add(ScanSource source, EsclInputCaps? input)
        {
            if (input is null) return;
            sources.Add(new SourceCapabilities(
                source,
                new SizeInches(input.MaxWidth / 300.0, input.MaxHeight / 300.0),
                input.Resolutions,
                input.ColorModes.Select(ToColorMode).OfType<ColorMode>().Distinct().ToList(),
                caps.Brightness is { } b ? new ValueRange(b.Min, b.Max) : null,
                caps.Contrast is { } c ? new ValueRange(c.Min, c.Max) : null));
        }

        Add(ScanSource.Flatbed, caps.Platen);
        Add(ScanSource.Feeder, caps.AdfSimplex ?? caps.AdfDuplex);
        Add(ScanSource.Duplex, caps.AdfDuplex);
        return new ScannerCapabilities(sources);
    }

    private static ColorMode? ToColorMode(string mode) => mode switch
    {
        "RGB24" or "RGB48" => ColorMode.Color,
        "Grayscale8" or "Grayscale16" => ColorMode.Grayscale,
        "BlackAndWhite1" => ColorMode.BlackAndWhite,
        _ => null,
    };

    /// <summary>The eSCL colour mode to request, and what the app will receive.</summary>
    private static (string Mode, ColorMode Delivered) PickColorMode(EsclInputCaps input, ColorMode requested)
    {
        bool Has(string mode) => input.ColorModes.Count == 0 || input.ColorModes.Contains(mode);

        if (requested == ColorMode.BlackAndWhite && input.ColorModes.Contains("BlackAndWhite1"))
            return ("BlackAndWhite1", ColorMode.BlackAndWhite);
        // Black & white falls back to grayscale; the app thresholds it afterwards.
        if (requested != ColorMode.Color && Has("Grayscale8"))
            return ("Grayscale8", ColorMode.Grayscale);
        return ("RGB24", ColorMode.Color);
    }

    private static string PickFormat(EsclInputCaps input)
    {
        // JPEG is universally supported and far smaller than raw data over the network.
        foreach (var format in new[] { "image/jpeg", "image/png" })
            if (input.DocumentFormats.Contains(format)) return format;
        return "image/jpeg";
    }

    private static int MapPercent(EsclRange range, int percent) =>
        new ValueRange(range.Min, range.Max).MapFromPercent(percent);

    private static async Task EnsureFeederReadyAsync(EsclClient client, CancellationToken ct)
    {
        // Without a status the job itself reports any problem.
        if (await TryGetStatusAsync(client, ct) is { } status && FeederProblem(status) is { } problem)
            throw problem;
    }

    /// <summary>
    /// The scanner's status, or null when it can't be had quickly. Status only refines messages, so a
    /// scanner that doesn't answer must not hold up the scan or the error for the full page timeout.
    /// </summary>
    private static async Task<EsclStatus?> TryGetStatusAsync(EsclClient client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QuickRequestTimeout);
        try
        {
            return await client.GetStatusAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or EsclHttpException or FormatException or System.Xml.XmlException
                                   || IsTimeout(ex, ct))
        {
            return null;
        }
    }

    private static ScannerException? FeederProblem(EsclStatus status) => status.AdfState switch
    {
        "ScannerAdfEmpty" => new("The document feeder is empty. Load the pages and try again.", ScannerErrorKind.FeederEmpty),
        "ScannerAdfJam" => new("Paper jam in the document feeder. Clear it and try again.", ScannerErrorKind.PaperJam),
        "ScannerAdfDoorOpen" => new("The document feeder cover is open. Close it and try again.", ScannerErrorKind.CoverOpen),
        "ScannerAdfMispick" or "ScannerAdfMultipickDetected" =>
            new("The feeder picked up the paper incorrectly. Straighten the pages and try again.", ScannerErrorKind.PaperProblem),
        "ScannerAdfInputTrayFailed" => new("The document feeder failed. Check the scanner.", ScannerErrorKind.PaperProblem),
        _ => null,
    };

    /// <summary>Turns a failed request into the most specific message the scanner's status allows.</summary>
    private static async Task<ScannerException> ExplainAsync(EsclClient client, Exception error, bool feeder)
    {
        if (error is HttpRequestException or OperationCanceledException)
            return Unreachable(client, error);

        if (await TryGetStatusAsync(client, CancellationToken.None) is { } status)
        {
            if (feeder && FeederProblem(status) is { } problem) return problem;
            if (status.State == "Processing")
                return new ScannerException("The scanner is busy. Wait for it to finish and try again.", ScannerErrorKind.Busy, error);
        }

        return error is EsclHttpException { Status: HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable }
            ? new ScannerException("The scanner is busy or not ready. Try again in a moment.", ScannerErrorKind.Busy, error)
            : new ScannerException(error.Message, ScannerErrorKind.General, error);
    }

    /// <summary>HttpClient reports its timeout as a cancellation; only the caller's token means the user canceled.</summary>
    private static bool IsTimeout(Exception ex, CancellationToken ct) =>
        ex is OperationCanceledException && !ct.IsCancellationRequested;

    private static ScannerException Unreachable(EsclClient client, Exception error) => new(
        $"The scanner can't be reached at {client.BaseUri.Host}. Check that it is turned on and connected to the network.",
        ScannerErrorKind.Offline, error);
}
