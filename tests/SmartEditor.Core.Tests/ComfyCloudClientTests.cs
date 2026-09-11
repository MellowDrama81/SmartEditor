using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;
using SmartEditor.Core.Services;
using SmartEditor.Core.Tests.TestSupport;
using Xunit;

namespace SmartEditor.Core.Tests;

public class ComfyCloudClientTests
{
    private static WorkflowDefinition LoadWorkflow(string id) =>
        new FileWorkflowCatalog(Path.Combine(AppContext.BaseDirectory, "Workflows"))
            .GetAll().Single(w => w.Id == id);

    private static ComfyCloudClient MakeClient(FakeCloudServerHandler handler, string apiKey = "secret-key")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-comfy-cloud.test/") };
        return new ComfyCloudClient(http, Options.Create(new ComfyUiOptions
        {
            BaseUrl = "http://fake-comfy-cloud.test",
            Backend = ComfyUiBackend.Cloud,
            ApiKey = apiKey,
        }));
    }

    [Fact]
    public async Task Submits_via_the_api_prefixed_endpoints_with_the_api_key_header()
    {
        var handler = new FakeCloudServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("flux2-klein-edit-single");
        var image = new SourceImage("in.png", [1, 2, 3]);
        var request = new EditRequest([image], "make it blue");

        var result = await client.RunWorkflowAsync(workflow, request, "a vivid blue photo", null, null, CancellationToken.None);

        Assert.Equal(new byte[] { 9, 9, 9, 9 }, result.ResultBytes);
        Assert.Equal(1, handler.UploadCount);
        Assert.Contains("a vivid blue photo", handler.LastPromptBody);
        Assert.Contains("secret-key", handler.ApiKeysSeen);
    }

    [Fact]
    public async Task Does_not_forward_the_api_key_to_the_redirected_third_party_host()
    {
        var handler = new FakeCloudServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("z-image-turbo");
        var request = new EditRequest([], "a watercolor fox");

        await client.RunWorkflowAsync(workflow, request, "a watercolor fox in a forest", null, null, CancellationToken.None);

        // Every Cloud-hosted request should carry the key; the final storage.googleapis.com hop must not.
        Assert.Contains("secret-key", handler.ApiKeysSeen);
        Assert.Contains(null, handler.ApiKeysSeen);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("non_retryable_error")]
    [InlineData("lost")]
    [InlineData("cancelled")]
    public async Task Throws_when_the_job_reports_a_terminal_failure_status(string status)
    {
        var handler = new FakeCloudServerHandler { JobStatus = status };
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("z-image-turbo");
        var request = new EditRequest([], "a watercolor fox");

        var ex = await Assert.ThrowsAsync<ComfyWorkflowException>(
            () => client.RunWorkflowAsync(workflow, request, "a watercolor fox", null, null, CancellationToken.None));

        Assert.Contains(status, ex.Message);
    }
}
