using System.Text.Json;
using System.Text.RegularExpressions;
using SmartEditor.Core.Abstractions;

namespace SmartEditor.Core.Services;

/// <summary>Parses a JSON object out of raw LLM text that may be wrapped in a markdown code fence
/// or surrounded by extra prose, since structured-output enforcement is inconsistent across
/// OpenAI-compatible providers.</summary>
internal static partial class TolerantJson
{
    public static T Parse<T>(string rawText, JsonSerializerOptions options)
    {
        var candidate = ExtractJsonBlock(rawText);
        try
        {
            return JsonSerializer.Deserialize<T>(candidate, options)
                   ?? throw new LlmResponseParseException("LLM response parsed to null.", rawText);
        }
        catch (JsonException ex)
        {
            throw new LlmResponseParseException($"Could not parse LLM response as JSON: {ex.Message}", rawText);
        }
    }

    private static string ExtractJsonBlock(string text)
    {
        var fenced = FencedJsonRegex().Match(text);
        if (fenced.Success)
        {
            return fenced.Groups[1].Value.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return text[start..(end + 1)];
        }

        return text.Trim();
    }

    [GeneratedRegex(@"```(?:json)?\s*(\{.*?\})\s*```", RegexOptions.Singleline)]
    private static partial Regex FencedJsonRegex();
}
