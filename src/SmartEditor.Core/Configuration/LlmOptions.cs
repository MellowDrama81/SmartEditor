namespace SmartEditor.Core.Configuration;

public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    /// <summary>Base URL of an OpenAI-compatible chat-completions API, e.g.
    /// https://openrouter.ai/api/v1 or https://api.deepinfra.com/v1/openai.</summary>
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    public string ApiKey { get; set; } = "";

    /// <summary>Model id as understood by the configured provider (e.g. an OpenRouter or
    /// DeepInfra model slug).</summary>
    public string Model { get; set; } = "";

    /// <summary>Whether the configured provider/model route reliably supports strict
    /// <c>response_format: json_schema</c>. When false, the client falls back to the more
    /// broadly supported <c>json_object</c> mode.</summary>
    public bool SupportsJsonSchema { get; set; }
}
