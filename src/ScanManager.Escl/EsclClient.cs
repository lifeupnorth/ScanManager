using System.Net;
using System.Text;

namespace ScanManager.Escl;

/// <summary>An eSCL request the scanner answered with an unexpected HTTP status.</summary>
public sealed class EsclHttpException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// The eSCL HTTP protocol: read capabilities and status, create a scan job, then fetch
/// one document (page image) at a time until the scanner answers 404 (no more pages).
/// </summary>
public sealed class EsclClient
{
    private static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(1);
    private const int MaxBusyRetries = 60;

    private readonly HttpClient _http;

    public EsclClient(HttpClient http, Uri baseUri)
    {
        _http = http;
        // Relative paths only combine correctly when the base ends with a slash.
        BaseUri = baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/");
    }

    public Uri BaseUri { get; }

    public async Task<EsclCapabilities> GetCapabilitiesAsync(CancellationToken ct = default) =>
        EsclXml.ParseCapabilities(await GetStringAsync("ScannerCapabilities", ct));

    public async Task<EsclStatus> GetStatusAsync(CancellationToken ct = default) =>
        EsclXml.ParseStatus(await GetStringAsync("ScannerStatus", ct));

    /// <summary>Starts a scan job and returns its URI.</summary>
    public async Task<Uri> CreateJobAsync(EsclScanRequest request, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var content = new StringContent(EsclXml.BuildScanSettings(request), Encoding.UTF8, "text/xml");
            using var response = await _http.PostAsync(new Uri(BaseUri, "ScanJobs"), content, ct);

            // 503: still busy with a previous job or warming up.
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < MaxBusyRetries)
            {
                await Task.Delay(BusyRetryDelay, ct);
                continue;
            }
            if (response.StatusCode != HttpStatusCode.Created)
                throw new EsclHttpException(response.StatusCode, $"The scanner refused the scan job (HTTP {(int)response.StatusCode}).");

            var location = response.Headers.Location
                ?? throw new EsclHttpException(response.StatusCode, "The scanner did not return a job address.");
            return location.IsAbsoluteUri ? location : new Uri(BaseUri, location);
        }
    }

    /// <summary>Downloads the next page of the job, or returns null when there are no more.</summary>
    public async Task<byte[]?> NextDocumentAsync(Uri job, CancellationToken ct = default)
    {
        var uri = new Uri(job.AbsoluteUri.TrimEnd('/') + "/NextDocument");
        for (int attempt = 0; ; attempt++)
        {
            using var response = await _http.GetAsync(uri, ct);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    return await response.Content.ReadAsByteArrayAsync(ct);
                case HttpStatusCode.NotFound:
                    return null;
                case HttpStatusCode.ServiceUnavailable when attempt < MaxBusyRetries:
                    await Task.Delay(BusyRetryDelay, ct);
                    continue;
                default:
                    throw new EsclHttpException(response.StatusCode, $"The scanner failed to deliver the page (HTTP {(int)response.StatusCode}).");
            }
        }
    }

    /// <summary>Cancels or cleans up a job. Never throws: the job may already be gone.</summary>
    public async Task CancelJobAsync(Uri job)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var _ = await _http.DeleteAsync(job, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { }
    }

    private async Task<string> GetStringAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(new Uri(BaseUri, path), ct);
        if (!response.IsSuccessStatusCode)
            throw new EsclHttpException(response.StatusCode, $"The scanner answered HTTP {(int)response.StatusCode} for {path}.");
        return await response.Content.ReadAsStringAsync(ct);
    }
}
