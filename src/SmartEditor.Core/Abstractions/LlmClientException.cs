namespace SmartEditor.Core.Abstractions;

/// <summary>A transport-level or malformed-response failure talking to the LLM provider.</summary>
public sealed class LlmClientException : Exception
{
    public LlmClientException(string message) : base(message) { }
    public LlmClientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The LLM responded, but its content couldn't be parsed as the expected JSON shape even
/// after tolerant cleanup (code-fence stripping, etc).</summary>
public sealed class LlmResponseParseException : Exception
{
    public string RawResponse { get; }

    public LlmResponseParseException(string message, string rawResponse) : base(message)
    {
        RawResponse = rawResponse;
    }
}
