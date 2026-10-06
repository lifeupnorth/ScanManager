using System.Net;
using System.Text;
using System.Xml.Linq;
using ScanManager.Core.Geometry;
using ScanManager.Core.Scanning;
using ScanManager.Escl;

namespace ScanManager.Core.Tests;

public class EsclTests
{
    // Trimmed from a real Canon MF741C response (identifying details removed).
    private const string CapabilitiesXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <scan:ScannerCapabilities xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm" xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03">
          <pwg:Version>2.63</pwg:Version>
          <pwg:MakeAndModel>Test MFP</pwg:MakeAndModel>
          <scan:Platen>
            <scan:PlatenInputCaps>
              <scan:MinWidth>32</scan:MinWidth><scan:MaxWidth>2551</scan:MaxWidth>
              <scan:MinHeight>32</scan:MinHeight><scan:MaxHeight>3508</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile>
                <scan:ColorModes><scan:ColorMode>Grayscale8</scan:ColorMode><scan:ColorMode>RGB24</scan:ColorMode></scan:ColorModes>
                <scan:DocumentFormats>
                  <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat><pwg:DocumentFormat>application/pdf</pwg:DocumentFormat>
                  <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>
                </scan:DocumentFormats>
                <scan:SupportedResolutions><scan:DiscreteResolutions>
                  <scan:DiscreteResolution><scan:XResolution>150</scan:XResolution><scan:YResolution>150</scan:YResolution></scan:DiscreteResolution>
                  <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>300</scan:YResolution></scan:DiscreteResolution>
                </scan:DiscreteResolutions></scan:SupportedResolutions>
              </scan:SettingProfile></scan:SettingProfiles>
            </scan:PlatenInputCaps>
          </scan:Platen>
          <scan:Adf>
            <scan:AdfSimplexInputCaps>
              <scan:MaxWidth>2551</scan:MaxWidth><scan:MaxHeight>4200</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile>
                <scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode></scan:ColorModes>
                <scan:SupportedResolutions><scan:ResolutionRange>
                  <scan:XResolutionRange><scan:Min>100</scan:Min><scan:Max>600</scan:Max><scan:Step>100</scan:Step></scan:XResolutionRange>
                </scan:ResolutionRange></scan:SupportedResolutions>
              </scan:SettingProfile></scan:SettingProfiles>
            </scan:AdfSimplexInputCaps>
            <scan:AdfDuplexInputCaps>
              <scan:MaxWidth>2551</scan:MaxWidth><scan:MaxHeight>4200</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile>
                <scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode></scan:ColorModes>
                <scan:SupportedResolutions><scan:DiscreteResolutions>
                  <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>300</scan:YResolution></scan:DiscreteResolution>
                </scan:DiscreteResolutions></scan:SupportedResolutions>
              </scan:SettingProfile></scan:SettingProfiles>
            </scan:AdfDuplexInputCaps>
          </scan:Adf>
          <scan:BrightnessSupport><scan:Min>0</scan:Min><scan:Max>100</scan:Max><scan:Normal>50</scan:Normal></scan:BrightnessSupport>
        </scan:ScannerCapabilities>
        """;

    private static string Status(string adfState) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <scan:ScannerStatus xmlns:pwg="http://www.pwg.org/schemas/2010/12/sm" xmlns:scan="http://schemas.hp.com/imaging/escl/2011/05/03">
          <pwg:Version>2.63</pwg:Version><pwg:State>Idle</pwg:State><scan:AdfState>{adfState}</scan:AdfState>
        </scan:ScannerStatus>
        """;

    [Fact]
    public void Capabilities_are_parsed_per_input()
    {
        var caps = EsclXml.ParseCapabilities(CapabilitiesXml);

        Assert.Equal("2.63", caps.Version);
        Assert.Equal(2551, caps.Platen!.MaxWidth);
        Assert.Equal(3508, caps.Platen.MaxHeight);
        Assert.Equal([150, 300], caps.Platen.Resolutions);
        Assert.Equal(["Grayscale8", "RGB24"], caps.Platen.ColorModes);
        Assert.Contains("image/jpeg", caps.Platen.DocumentFormats);
        Assert.Equal([100, 200, 300, 400, 600], caps.AdfSimplex!.Resolutions);
        Assert.NotNull(caps.AdfDuplex);
        Assert.Equal(new EsclRange(0, 100, 50), caps.Brightness);
        Assert.Null(caps.Contrast);
    }

    [Fact]
    public void Capabilities_map_to_sources_and_sizes()
    {
        var caps = EsclScannerBackend.ToScannerCapabilities(EsclXml.ParseCapabilities(CapabilitiesXml));

        Assert.Equal([ScanSource.Flatbed, ScanSource.Feeder, ScanSource.Duplex], caps.Sources.Select(s => s.Source));
        var flatbed = caps.For(ScanSource.Flatbed)!;
        Assert.Equal(8.503, flatbed.MaxSize.Width, 3);
        Assert.Equal(11.693, flatbed.MaxSize.Height, 3);
        Assert.Equal([ColorMode.Grayscale, ColorMode.Color], flatbed.NativeColorModes);
        Assert.Equal(new ValueRange(0, 100), flatbed.Brightness);
    }

    [Fact]
    public void Scan_settings_document_has_region_source_and_duplex()
    {
        string xml = EsclXml.BuildScanSettings(new EsclScanRequest(
            "2.63", "Feeder", true, "Grayscale8", 300, 0, 0, 2550, 3300, "image/jpeg", null, null));
        var doc = XDocument.Parse(xml);
        var scan = EsclXml.Scan;
        var pwg = EsclXml.Pwg;

        Assert.Equal("Feeder", doc.Root!.Element(pwg + "InputSource")!.Value);
        Assert.Equal("true", doc.Root.Element(scan + "Duplex")!.Value);
        Assert.Equal("Grayscale8", doc.Root.Element(scan + "ColorMode")!.Value);
        var region = doc.Root.Element(pwg + "ScanRegions")!.Element(pwg + "ScanRegion")!;
        Assert.Equal("escl:ThreeHundredthsOfInches", region.Element(pwg + "ContentRegionUnits")!.Value);
        Assert.Equal("2550", region.Element(pwg + "Width")!.Value);
        Assert.Equal("3300", region.Element(pwg + "Height")!.Value);
        Assert.Equal("image/jpeg", doc.Root.Element(scan + "DocumentFormatExt")!.Value);
        Assert.Null(doc.Root.Element(scan + "Brightness"));
    }

    [Fact]
    public void Old_devices_get_no_DocumentFormatExt_and_platen_jobs_no_duplex_flag()
    {
        var doc = XDocument.Parse(EsclXml.BuildScanSettings(new EsclScanRequest(
            "2.0", "Platen", false, "RGB24", 150, 10, 20, 300, 400, "image/jpeg", 60, null)));
        Assert.Null(doc.Root!.Element(EsclXml.Scan + "DocumentFormatExt"));
        Assert.Null(doc.Root.Element(EsclXml.Scan + "Duplex"));
        Assert.Equal("60", doc.Root.Element(EsclXml.Scan + "Brightness")!.Value);
    }

    [Fact]
    public async Task Duplex_job_returns_every_page_until_the_scanner_says_done()
    {
        var scanner = new FakeScanner { Pages = 2 };
        using var backend = await ConnectAsync(scanner);

        var pages = new List<ScannedImage>();
        await foreach (var page in backend.ScanAsync("dev", new ScanSettings { Source = ScanSource.Duplex, Dpi = 300 }))
            pages.Add(page);

        Assert.Equal([1, 2], pages.Select(p => p.PageNumber));
        Assert.Contains("<scan:Duplex>true</scan:Duplex>", scanner.LastJobXml);
        Assert.Contains("<pwg:InputSource>Feeder</pwg:InputSource>", scanner.LastJobXml);
        Assert.False(scanner.JobDeleted); // the scanner finished the job itself
    }

    [Fact]
    public async Task Empty_feeder_is_reported_before_starting_a_job()
    {
        var scanner = new FakeScanner { AdfState = "ScannerAdfEmpty" };
        using var backend = await ConnectAsync(scanner);

        var ex = await Assert.ThrowsAsync<ScannerException>(async () =>
        {
            await foreach (var _ in backend.ScanAsync("dev", new ScanSettings { Source = ScanSource.Feeder })) { }
        });
        Assert.Equal(ScannerErrorKind.FeederEmpty, ex.Kind);
        Assert.Null(scanner.LastJobXml);
    }

    [Fact]
    public async Task Flatbed_scan_takes_one_page_requests_the_area_and_cleans_up()
    {
        var scanner = new FakeScanner { Pages = 1 };
        using var backend = await ConnectAsync(scanner);

        var settings = new ScanSettings { Source = ScanSource.Flatbed, Dpi = 200, Area = new ScanArea(1, 2, 3, 4) };
        var pages = new List<ScannedImage>();
        await foreach (var page in backend.ScanAsync("dev", settings))
            pages.Add(page);

        Assert.Single(pages);
        Assert.Equal(150, pages[0].Dpi); // closest supported resolution
        Assert.Contains("<pwg:XOffset>300</pwg:XOffset>", scanner.LastJobXml);
        Assert.Contains("<pwg:YOffset>600</pwg:YOffset>", scanner.LastJobXml);
        Assert.Contains("<pwg:Width>900</pwg:Width>", scanner.LastJobXml);
        Assert.Contains("<pwg:Height>1200</pwg:Height>", scanner.LastJobXml);
        Assert.True(scanner.JobDeleted);
    }

    [Fact]
    public async Task Unreachable_scanners_are_left_out_of_the_device_list()
    {
        var source = new FixedSource(new EsclDevice("dev", "Test MFP", new Uri("http://scanner.test/eSCL")));
        using var http = new HttpClient(new FailingHandler());
        using var backend = new EsclScannerBackend(source, http);
        Assert.Empty(await backend.GetDevicesAsync());
    }

    private static async Task<EsclScannerBackend> ConnectAsync(FakeScanner scanner)
    {
        var source = new FixedSource(new EsclDevice("dev", "Test MFP", new Uri("http://scanner.test/eSCL")));
        var backend = new EsclScannerBackend(source, new HttpClient(scanner));
        Assert.Single(await backend.GetDevicesAsync());
        return backend;
    }

    private sealed class FixedSource(params EsclDevice[] devices) : IEsclDeviceSource
    {
        public Task<IReadOnlyList<EsclDevice>> FindDevicesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<EsclDevice>>(devices);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No route to host");
    }

    /// <summary>An in-memory eSCL scanner.</summary>
    private sealed class FakeScanner : HttpMessageHandler
    {
        public int Pages { get; init; }
        public string AdfState { get; init; } = "ScannerAdfLoaded";
        public string? LastJobXml { get; private set; }
        public bool JobDeleted { get; private set; }
        private int _delivered;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/eSCL/ScannerCapabilities")
                return Xml(CapabilitiesXml);
            if (request.Method == HttpMethod.Get && path == "/eSCL/ScannerStatus")
                return Xml(Status(AdfState));
            if (request.Method == HttpMethod.Post && path == "/eSCL/ScanJobs")
            {
                LastJobXml = await request.Content!.ReadAsStringAsync(ct);
                var created = new HttpResponseMessage(HttpStatusCode.Created);
                created.Headers.Location = new Uri("/eSCL/ScanJobs/42", UriKind.Relative);
                return created;
            }
            if (request.Method == HttpMethod.Get && path == "/eSCL/ScanJobs/42/NextDocument")
            {
                if (_delivered >= Pages) return new HttpResponseMessage(HttpStatusCode.NotFound);
                _delivered++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xD8, (byte)_delivered]) };
            }
            if (request.Method == HttpMethod.Delete && path == "/eSCL/ScanJobs/42")
            {
                JobDeleted = true;
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Xml(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/xml") };
    }
}
