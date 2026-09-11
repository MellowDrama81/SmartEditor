using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;

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

    public ComfyCloudClient(HttpClient http, IOptions<ComfyUiOptions> options)
    {
        _http = http;
        _apiKey = options.Value.ApiKey;
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

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
