using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;

namespace SmartEditor.Core.Services;

/// <summary>Talks to any OpenAI-compatible chat-completions API (OpenRouter, DeepInfra, OpenAI
/// itself, etc). Handles multimodal (text + image) messages and opportunistic structured JSON
/// output, degrading gracefully across providers with inconsistent support for either.</summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OpenAiCompatibleLlmClient(HttpClient http, IOptions<LlmOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<string> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var messages = new JsonArray { BuildMessage(LlmRole.System, request.SystemPrompt, null) };
        foreach (var message in request.Messages)
        {
            messages.Add(BuildMessage(message.Role, message.Text, message.ImageBytes));
        }

        var body = new JsonObject
        {
            ["model"] = _options.Model,
            ["messages"] = messages,
        };

        if (request.ResponseSchema is { } schema)
        {
            body["response_format"] = _options.SupportsJsonSchema
                ? new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject
                    {
                        ["name"] = schema.Name,
                        ["strict"] = false,
                        ["schema"] = schema.Schema.DeepClone(),
                    },
                }
                : new JsonObject { ["type"] = "json_object" };
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(body),
        };
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        string responseText;
        try
        {
            response = await _http.SendAsync(httpRequest, ct);
            responseText = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmClientException($"Could not reach the LLM provider at {_http.BaseAddress}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmClientException($"LLM request failed ({(int)response.StatusCode} {response.StatusCode}): {responseText}");
        }

        JsonNode responseNode;
        try
        {
            responseNode = JsonNode.Parse(responseText) ?? throw new LlmClientException("Empty LLM response.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new LlmClientException($"LLM response was not valid JSON: {ex.Message}", ex);
        }

        var messageNode = responseNode["choices"]?[0]?["message"];
        var content = messageNode?["content"]?.GetValue<string>();

        // Some reasoning models (via certain OpenRouter/DeepInfra routes) leave `content` empty
        // and put the entire answer in `reasoning`/`reasoning_content` instead of separating
        // chain-of-thought from the final answer. Fall back to those fields in that case, since
        // callers only ever get a single string back from this client.
        if (string.IsNullOrWhiteSpace(content))
        {
            content = messageNode?["reasoning"]?.GetValue<string>() ?? messageNode?["reasoning_content"]?.GetValue<string>();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new LlmClientException($"Unexpected LLM response shape (no usable content or reasoning): {responseText}");
        }

        return content;
    }

    private static JsonObject BuildMessage(LlmRole role, string text, IReadOnlyList<byte[]>? imageBytes)
    {
        var roleName = role switch
        {
            LlmRole.System => "system",
            LlmRole.User => "user",
            LlmRole.Assistant => "assistant",
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };

        if (imageBytes is null or { Count: 0 })
        {
            return new JsonObject { ["role"] = roleName, ["content"] = text };
        }

        var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var bytes in imageBytes)
        {
            var dataUri = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";
            content.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = dataUri },
            });
        }

        return new JsonObject { ["role"] = roleName, ["content"] = content };
    }
}
