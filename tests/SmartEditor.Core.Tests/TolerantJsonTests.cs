using System.Text.Json;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Services;
using Xunit;

namespace SmartEditor.Core.Tests;

public class TolerantJsonTests
{
    private sealed record Sample(string Name, int Count);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void Parses_plain_json()
    {
        var result = TolerantJson.Parse<Sample>("""{"name":"foo","count":3}""", Options);
        Assert.Equal("foo", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Strips_markdown_code_fences()
    {
        var raw = "Here you go:\n```json\n{\"name\":\"foo\",\"count\":3}\n```\nLet me know if that works.";
        var result = TolerantJson.Parse<Sample>(raw, Options);
        Assert.Equal("foo", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Extracts_json_surrounded_by_prose_without_fences()
    {
        var raw = "Sure! {\"name\":\"foo\",\"count\":3} That should do it.";
        var result = TolerantJson.Parse<Sample>(raw, Options);
        Assert.Equal("foo", result.Name);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Throws_a_dedicated_parse_exception_on_garbage()
    {
        var ex = Assert.Throws<LlmResponseParseException>(() => TolerantJson.Parse<Sample>("not json at all", Options));
        Assert.Equal("not json at all", ex.RawResponse);
    }
}
