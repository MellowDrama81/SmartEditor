using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;

namespace SmartEditor.Core.Services;

/// <summary>Talks to a plain self-hosted ComfyUI server: unprefixed <c>/prompt</c>,
/// <c>/history/{id}</c>, <c>/upload/image</c>, <c>/view</c> endpoints, no authentication. A
/// WebSocket connection is opened purely to report best-effort progress &mdash; it is never used
/// to decide completion, since long-lived socket lifecycle is unreliable on Android.</summary>
public sealed class ComfyUiClient : ComfyUiClientBase
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private readonly HttpClient _http;
    private readonly ComfyUiOptions _options;

    public ComfyUiClient(HttpClient http, IOptions<ComfyUiOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    protected override async Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var uniqueName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";

        using var content = new MultipartFormDataContent();
        var imageContent = new ByteArrayContent(bytes);
        imageContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(imageContent, "image", uniqueName);
        content.Add(new StringContent("input"), "type");

        using var response = await _http.PostAsync("upload/image", content, ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"ComfyUI image upload failed ({(int)response.StatusCode}): {responseText}");
        }

        var node = JsonNode.Parse(responseText);
        var name = node?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name))
        {
            throw new ComfyWorkflowException($"ComfyUI image upload returned an unexpected response: {responseText}");
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

        using var response = await _http.PostAsync("prompt", JsonContent.Create(body), ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"ComfyUI rejected the workflow (invalid graph/placeholder binding): {responseText}");
        }

        var node = JsonNode.Parse(responseText);
        var promptId = node?["prompt_id"]?.GetValue<string>();
        if (string.IsNullOrEmpty(promptId))
        {
            throw new ComfyWorkflowException($"ComfyUI did not return a prompt_id: {responseText}");
        }

        return promptId;
    }

    protected override async Task<JsonNode> WaitForCompletionAsync(string promptId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + MaxWait;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using var response = await _http.GetAsync($"history/{promptId}", ct);
            if (response.IsSuccessStatusCode)
            {
                var responseText = await response.Content.ReadAsStringAsync(ct);
                var root = JsonNode.Parse(responseText);
                var entry = root?[promptId];
                if (entry is not null)
                {
                    var statusStr = entry["status"]?["status_str"]?.GetValue<string>();
                    var completed = entry["status"]?["completed"]?.GetValue<bool>() ?? false;

                    if (statusStr == "error")
                    {
                        throw new ComfyWorkflowException($"ComfyUI execution failed for prompt '{promptId}': {entry["status"]}");
                    }

                    if (completed && entry["outputs"] is JsonNode outputs)
                    {
                        return outputs;
                    }
                }
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new ComfyWorkflowException($"Timed out after {MaxWait.TotalMinutes:0} minutes waiting for ComfyUI to finish prompt '{promptId}'.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    protected override async Task<byte[]> FetchImageAsync(string filename, string subfolder, string type, CancellationToken ct)
    {
        var query = $"view?filename={Uri.EscapeDataString(filename)}&subfolder={Uri.EscapeDataString(subfolder)}&type={Uri.EscapeDataString(type)}";
        using var response = await _http.GetAsync(query, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Could not fetch result image '{filename}' ({(int)response.StatusCode}).");
        }

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    protected override async Task<JsonNode> FetchObjectInfoAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("object_info", ct);
        var responseText = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ComfyWorkflowException($"Could not fetch ComfyUI's object_info ({(int)response.StatusCode}).");
        }

        return JsonNode.Parse(responseText) ?? throw new ComfyWorkflowException("ComfyUI's object_info response was empty.");
    }

    protected override Task ReportProgressAsync(string clientId, IProgress<double> progress, CancellationToken ct) =>
        ListenForProgressAsync(clientId, progress, ct);

    private async Task ListenForProgressAsync(string clientId, IProgress<double> progress, CancellationToken ct)
    {
        try
        {
            var wsUri = new UriBuilder(_options.BaseUrl)
            {
                Scheme = _options.BaseUrl.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
                Path = "ws",
                Query = $"clientId={clientId}",
            }.Uri;

            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(wsUri, ct);

            var buffer = new byte[8192];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var stream = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        stream.Write(buffer, 0, result.Count);
                    }
                } while (!result.EndOfMessage);

                if (stream.Length == 0)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(stream.ToArray());
                var node = JsonNode.Parse(text);
                if (node?["type"]?.GetValue<string>() == "progress")
                {
                    var value = node["data"]?["value"]?.GetValue<double>();
                    var max = node["data"]?["max"]?.GetValue<double>();
                    if (value is not null && max is > 0)
                    {
                        progress.Report(value.Value / max.Value);
                    }
                }
            }
        }
        catch
        {
            // Progress reporting is best-effort; completion is always determined via /history.
        }
    }
}
