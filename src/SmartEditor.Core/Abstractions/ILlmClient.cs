using System.Text.Json.Nodes;

namespace SmartEditor.Core.Abstractions;

public enum LlmRole
{
    System,
    User,
    Assistant,
}

/// <summary>One message in an LLM conversation. Images are raw encoded bytes (JPEG/PNG); the
/// client implementation handles base64/data-URI encoding.</summary>
public sealed record LlmMessage(LlmRole Role, string Text, IReadOnlyList<byte[]>? ImageBytes = null);

/// <summary>A JSON Schema describing the expected structured response. Used opportunistically
/// (<c>response_format: json_schema</c>) when the configured provider/model is known to support
/// it; otherwise the client falls back to the broader <c>json_object</c> mode. Either way, the
/// caller must also spell out the exact expected shape in the prompt text &mdash; structured-output
/// enforcement is inconsistent across OpenAI-compatible providers, so this is belt-and-suspenders
/// rather than a hard dependency.</summary>
public sealed record JsonResponseSchema(string Name, JsonNode Schema);

/// <summary>A request to a vision-capable chat-completion LLM.</summary>
public sealed record LlmRequest(string SystemPrompt, IReadOnlyList<LlmMessage> Messages, JsonResponseSchema? ResponseSchema = null);

/// <summary>Thin transport abstraction over a vision-capable chat-completion API. Returns raw
/// text; JSON parsing/tolerance is the caller's responsibility.</summary>
public interface ILlmClient
{
    Task<string> CompleteAsync(LlmRequest request, CancellationToken ct);
}
