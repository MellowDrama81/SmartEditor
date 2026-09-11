using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Talks to Comfy Cloud (<c>cloud.comfy.org</c>): every endpoint lives under an
/// <c>/api/</c> prefix, auth is a plain <c>X-API-Key</c> header, job status is polled via
/// <c>GET /api/jobs/{id}</c> (a flat <c>status</c> string, not the nested
/// <c>{status_str, completed}</c> shape self-hosted ComfyUI uses), and <c>/api/view</c>
/// 302-redirects to a signed, time-limited <c>storage.googleapis.com</c> URL. The redirect is
/// followed manually (the injected <see cref="HttpClient"/> must have automatic redirects
/// disabled) so the API key header is never forwarded to that third-party host. No WebSocket is
/// available on Cloud, so progress reporting is not implemented for this backend.</summary>
public sealed class ComfyCloudClient : ComfyUiClientBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly int _assetPageSize;

    public ComfyCloudClient(HttpClient http, IOptions<ComfyUiOptions> options)
    {
        _http = http;
        _apiKey = options.Value.ApiKey;
        // The server hard-rejects (INVALID_LIMIT) anything over 500, confirmed live, so a bad
        // configured value can never break the request — it just gets clamped instead.
        _assetPageSize = Math.Clamp(options.Value.AssetPageSize, 1, 500);
    }

    private void ApplyAuth(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.TryAddWithoutValidation("X-API-Key", _apiKey);
        }
    }

    protected override async Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var uniqueName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/upload/image");
        ApplyAuth(request);

        var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(bytes);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(imageContent, "image", uniqueName);
        content.Add(new StringContent("input"), "type");
        request.Content = content;

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Comfy Cloud image upload failed ({(int)response.StatusCode}): {responseText}");
        }

        var node = JsonNode.Parse(responseText);
        var name = node?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            throw new ComfyWorkflowException($"Comfy Cloud image upload returned an unexpected response: {responseText}");
        }

        return name;
    }

    protected override async Task<string> SubmitPromptAsync(JsonNode graph, string clientId, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["prompt"] = graph,
            ["client_id"] = clientId,
        };
        // Partner (third-party API) nodes need the Cloud API key in extra_data too, in addition
        // to the request's own auth header.
        if (!string.IsNullOrEmpty(_apiKey))
        {
            body["extra_data"] = new JsonObject { ["api_key_comfy_org"] = _apiKey };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/prompt") { Content = JsonContent.Create(body) };
        ApplyAuth(request);

        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Comfy Cloud rejected the workflow submission: {responseText}");
        }

        var node = JsonNode.Parse(responseText);
        if (node?["node_errors"] is JsonObject nodeErrors && nodeErrors.Count > 0)
        {
            throw new ComfyWorkflowException($"Comfy Cloud rejected the workflow (invalid graph/placeholder binding): {nodeErrors}");
        }

        var promptId = node?["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(promptId))
        {
            throw new ComfyWorkflowException($"Comfy Cloud did not return a job id: {responseText}");
        }

        return promptId;
    }

    protected override async Task<JsonNode> WaitForCompletionAsync(string promptId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + MaxWait;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var request = new HttpRequestMessage(HttpMethod.Get, $"api/jobs/{Uri.EscapeDataString(promptId)}");
            ApplyAuth(request);
            using var response = await _http.SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(ct);
                var root = JsonNode.Parse(responseText);
                var status = root?["status"]?.GetValue<string>();

                switch (status)
                {
                    case "success" or "completed":
                        if (root?["outputs"] is JsonNode outputs)
                        {
                            return outputs;
                        }
                        throw new ComfyWorkflowException($"Comfy Cloud job '{promptId}' completed but reported no outputs.");
                    case "error" or "non_retryable_error" or "lost" or "cancelled":
                        throw new ComfyWorkflowException($"Comfy Cloud reported job '{promptId}' as '{status}'.");
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ComfyWorkflowException($"Timed out after {MaxWait.TotalMinutes:0} minutes waiting for Comfy Cloud to finish job '{promptId}'.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    protected override async Task<byte[]> FetchImageAsync(string filename, string subfolder, string type, CancellationToken ct)
    {
        var query = $"api/view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, query);
        ApplyAuth(request);
        using var response = await _http.SendAsync(request, ct);

        if (IsRedirect(response.StatusCode))
        {
            var location = response.Headers.Location
                            ?? throw new ComfyWorkflowException("Comfy Cloud's result redirect had no target URL.");

            // Deliberately no auth header on the redirect hop: it targets a different host
            // (a signed storage.googleapis.com URL), and the API key must not be sent there.
            using var redirectRequest = new HttpRequestMessage(HttpMethod.Get, location);
            using var redirectResponse = await _http.SendAsync(redirectRequest, ct);
            if (!redirectResponse.IsSuccessStatusCode)
            {
                throw new ComfyWorkflowException($"Could not download the Comfy Cloud result image ({(int)redirectResponse.StatusCode}).");
            }

            return await redirectResponse.Content.ReadAsByteArrayAsync(ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Could not fetch Comfy Cloud result image '{filename}' ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Comfy Cloud has a real, paginated asset-listing API (<c>GET /api/assets</c>,
    /// confirmed against a live account and Comfy's own docs as Cloud-only) with genuine display
    /// names, sizes, and timestamps &mdash; used instead of the generic <c>/object_info</c>
    /// fallback the base class uses for self-hosted ComfyUI. The API also returns an asset id, but
    /// that id is never what actually references the asset anywhere in ComfyUI (workflows, view,
    /// download all key on the filename) so it isn't surfaced here at all.
    ///
    /// Fetches exactly one page per call (the server's own <c>cursor</c>/<c>has_more</c> scheme,
    /// passed straight through as <see cref="AssetPage.NextCursor"/>) rather than following every
    /// page itself: a real account can have thousands of assets (one confirmed test account had
    /// ~4,900, ~10 pages at the API's 500-per-page max), and a caller like a scroll-to-load-more UI
    /// should only fetch the next batch once the viewer actually scrolls that far, not the whole
    /// listing up front.
    ///
    /// Despite the method's "input" name (shared with the self-hosted base-class implementation it
    /// overrides), this yields both "input" (uploaded source images) and "output" (prior generation
    /// results) tagged assets, so the same browser doubles as a way to reuse earlier results as new
    /// source images &mdash; and filters out every other tag (installed model weights, which can
    /// individually be tens of GB and must never be listed or thumbnailed here). Since a raw
    /// 500-asset batch can happen to contain few or no browsable ("input"/"output") entries, this
    /// keeps advancing through the server's own pages internally until it has at least one
    /// browsable asset to return or genuinely runs out (<c>has_more: false</c>), so a caller never
    /// sees a spurious empty-but-not-done page.</summary>
    public override async Task<AssetPage> ListInputAssetsPageAsync(string? cursor, CancellationToken ct)
    {
        var assets = new List<AssetInfo>();

        do
        {
            // sort=created_at&order=desc (newest first) is confirmed live as the server's own
            // default, but is passed explicitly rather than relied on implicitly.
            var query = $"api/assets?limit={_assetPageSize}&sort=created_at&order=desc" +
                        (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var request = new HttpRequestMessage(HttpMethod.Get, query);
            ApplyAuth(request);
            using var response = await _http.SendAsync(request, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new ComfyWorkflowException($"Could not list Comfy Cloud assets ({(int)response.StatusCode}): {responseText}");
            }

            var root = JsonNode.Parse(responseText)
                       ?? throw new ComfyWorkflowException("Comfy Cloud's asset list response was empty.");

            foreach (var asset in root["assets"]?.AsArray() ?? [])
            {
                // GET /api/assets returns EVERY asset in the account, not just images — confirmed
                // live it also includes installed model weights ("models"/"diffusion_models"/etc,
                // individually up to tens of GB). Only "input" (uploaded source images) and
                // "output" (prior generation results) tagged assets belong in the asset browser;
                // anything else here would list (and, via thumbnail loading downstream, try to
                // fetch) multi-gigabyte model files as if they were images, which is what made the
                // Assets tab hang.
                var tags = asset?["tags"]?.AsArray();
                var isBrowsable = tags is not null && tags.Any(t =>
                    string.Equals(t?.GetValue<string>(), "input", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t?.GetValue<string>(), "output", StringComparison.OrdinalIgnoreCase));
                if (!isBrowsable)
                {
                    continue;
                }

                // loader_path is the actual storage filename LoadImage/api/view need; name/display_name
                // are the human-facing labels and can differ from it (e.g. a hashed upload filename).
                var name = asset?["loader_path"]?.GetValue<string>() ?? asset?["name"]?.GetValue<string>();
                var displayName = asset?["display_name"]?.GetValue<string>() ?? name;
                if (name is not null)
                {
                    assets.Add(new AssetInfo(name, displayName ?? name));
                }
            }

            var hasMore = root["has_more"]?.GetValue<bool>() ?? false;
            cursor = hasMore ? root["next_cursor"]?.GetValue<string>() : null;
        } while (assets.Count == 0 && cursor is not null);

        return new AssetPage(assets, cursor);
    }

    protected override async Task<JsonNode> FetchObjectInfoAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/object_info");
        ApplyAuth(request);
        using var response = await _http.SendAsync(request, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Could not fetch Comfy Cloud's object_info ({(int)response.StatusCode}).");
        }

        return JsonNode.Parse(responseText) ?? throw new ComfyWorkflowException("Comfy Cloud's object_info response was empty.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
