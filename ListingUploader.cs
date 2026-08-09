using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using PfExplorer.Models;

namespace PfExplorer;

// Buffers mapped listings and flushes them to the server periodically in
// small batches instead of one HTTP request per listing.
public sealed class ListingUploader : IDisposable
{
    private const int FlushIntervalMs = 5000;
    private const int MaxBatchSize = 100;

    private readonly ConcurrentQueue<PfListingDto> _queue = new();
    // The outgoing batch itself isn't compressed (that's the request body,
    // not something AutomaticDecompression touches) — this just lets the
    // small ingest response {accepted, contributors} come back gzipped too,
    // same reasoning as AlertPoller._http.
    private readonly HttpClient _http = new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(10) };
    private readonly Configuration _config;
    private readonly IPluginLog _log;
    private readonly Timer _timer;

    public int TotalCaptured { get; private set; }
    public int TotalUploaded { get; private set; }
    public DateTime? LastUploadAt { get; private set; }
    public string? LastError { get; private set; }

    // From the ingest endpoint's own response — piggybacks on the upload
    // request this class already makes every FlushIntervalMs, rather than
    // needing a separate poll, so it updates regardless of whether alert
    // polling (AlertPoller, a different toggle) is on.
    public PfContributorStats? ContributorStats { get; private set; }

    public ListingUploader(Configuration config, IPluginLog log)
    {
        _config = config;
        _log = log;
        _timer = new Timer(_ => _ = FlushAsync(), null, FlushIntervalMs, FlushIntervalMs);
    }

    public void Enqueue(PfListingDto listing)
    {
        TotalCaptured++;
        _queue.Enqueue(listing);
    }

    private async Task FlushAsync()
    {
        if (!_config.Enabled || _queue.IsEmpty)
            return;

        var batch = new List<PfListingDto>(MaxBatchSize);
        while (batch.Count < MaxBatchSize && _queue.TryDequeue(out var listing))
            batch.Add(listing);

        if (batch.Count == 0)
            return;

        try
        {
            var payload = new IngestPayload { Listings = batch, ContributorId = _config.ContributorId };
            var json = JsonSerializer.Serialize(payload);
            using var response = await PostSignedAsync("/api/listings", json).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                LastError = $"HTTP {(int)response.StatusCode}: {body}";
                _log.Warning($"[PfExplorer] upload failed: {LastError}");
                return;
            }

            var responseBody = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var ingestResponse = await JsonSerializer.DeserializeAsync<IngestResponse>(responseBody).ConfigureAwait(false);
            if (ingestResponse?.Contributors != null)
                ContributorStats = ingestResponse.Contributors;

            TotalUploaded += batch.Count;
            LastUploadAt = DateTime.UtcNow;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Warning(ex, "[PfExplorer] upload failed");
        }
    }

    // Reports listing_ids that PfScanTracker's own scan (fed purely by your
    // organic PF browsing/clicks) positively confirmed are gone — see
    // AlertPoller.PruneMissing, an unfilled category page not containing
    // them, ground truth from the game itself — to the server, so they're
    // marked expired for every other client's poll too instead of just this
    // one's local Matches. Fire-and-forget, same reasoning as FlushAsync
    // being timer- rather than await-driven.
    public async Task ExpireAsync(IReadOnlyList<string> listingIds)
    {
        if (!_config.Enabled || listingIds.Count == 0)
            return;

        try
        {
            var payload = new ExpirePayload { ListingIds = listingIds.ToList(), ContributorId = _config.ContributorId };
            var json = JsonSerializer.Serialize(payload);
            using var response = await PostSignedAsync("/api/listings/expire", json).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                _log.Warning($"[PfExplorer] expire failed: HTTP {(int)response.StatusCode}: {body}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "[PfExplorer] expire failed");
        }
    }

    // Signed per-request (timestamp + nonce + this exact body) — see
    // IngestSigning and server/src/auth.ts's requireIngestSignature —
    // rather than a single static header that'd work forever if captured
    // once. Shared by FlushAsync and ExpireAsync since both hit
    // signature-gated POST routes the same way.
    private async Task<HttpResponseMessage> PostSignedAsync(string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var url = $"{_config.ServerUrl.TrimEnd('/')}{path}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };

        var (timestamp, nonce, signature) = IngestSigning.Sign(json);
        request.Headers.Add("x-timestamp", timestamp);
        request.Headers.Add("x-nonce", nonce);
        request.Headers.Add("x-signature", signature);

        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
