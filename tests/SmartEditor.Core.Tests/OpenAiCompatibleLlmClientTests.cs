using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Services;
using Xunit;

namespace SmartEditor.Core.Tests;

public class OpenAiCompatibleLlmClientTests
{
    private sealed class FakeLlmServerHandler : HttpMessageHandler
    {
        public string ResponseBody { get; set; } = "";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json"),
            });
    }

    private static OpenAiCompatibleLlmClient MakeClient(FakeLlmServerHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-llm.test/") };
        return new OpenAiCompatibleLlmClient(http, Options.Create(new LlmOptions { BaseUrl = "http://fake-llm.test", Model = "test-model" }));
    }

    private static string ChatCompletionsResponse(JsonObject message)
    {
        var body = new JsonObject
        {
            ["choices"] = new JsonArray { new JsonObject { ["message"] = message } },
        };
        return body.ToJsonString();
    }

    private static LlmRequest MakeRequest() => new("system prompt", [new LlmMessage(LlmRole.User, "hello")]);

    [Fact]
    public async Task Returns_content_when_present()
    {
        var handler = new FakeLlmServerHandler
        {
            ResponseBody = ChatCompletionsResponse(new JsonObject { ["content"] = """{"satisfied":true}""" }),
        };
        var client = MakeClient(handler);

        var result = await client.CompleteAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal("""{"satisfied":true}""", result);
    }

    [Fact]
    public async Task Falls_back_to_reasoning_when_content_is_empty()
    {
        var handler = new FakeLlmServerHandler
        {
            ResponseBody = ChatCompletionsResponse(new JsonObject
            {
                ["content"] = "",
                ["reasoning"] = """{"satisfied":true,"feedback":"looks great"}""",
            }),
        };
        var client = MakeClient(handler);

        var result = await client.CompleteAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal("""{"satisfied":true,"feedback":"looks great"}""", result);
    }

    [Fact]
    public async Task Falls_back_to_reasoning_when_content_is_missing_entirely()
    {
        var handler = new FakeLlmServerHandler
        {
            ResponseBody = ChatCompletionsResponse(new JsonObject
            {
                ["reasoning"] = """{"workflowId":"wf1","refinedPrompt":"x","reasoning":"y"}""",
            }),
        };
        var client = MakeClient(handler);

        var result = await client.CompleteAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal("""{"workflowId":"wf1","refinedPrompt":"x","reasoning":"y"}""", result);
    }

    [Fact]
    public async Task Falls_back_to_reasoning_content_when_reasoning_is_also_absent()
    {
        var handler = new FakeLlmServerHandler
        {
            ResponseBody = ChatCompletionsResponse(new JsonObject
            {
                ["reasoning_content"] = """{"satisfied":false}""",
            }),
        };
        var client = MakeClient(handler);

        var result = await client.CompleteAsync(MakeRequest(), CancellationToken.None);

        Assert.Equal("""{"satisfied":false}""", result);
    }

    [Fact]
    public async Task Throws_when_neither_content_nor_reasoning_is_usable()
    {
        var handler = new FakeLlmServerHandler
        {
            ResponseBody = ChatCompletionsResponse(new JsonObject { ["content"] = "" }),
        };
        var client = MakeClient(handler);

        await Assert.ThrowsAsync<LlmClientException>(() => client.CompleteAsync(MakeRequest(), CancellationToken.None));
    }
}
