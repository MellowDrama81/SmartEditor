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

    private static ComfyCloudClient MakeClient(FakeCloudServerHandler handler, string apiKey = "secret-key", int? assetPageSize = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-comfy-cloud.test/") };
        var options = new ComfyUiOptions
        {
            BaseUrl = "http://fake-comfy-cloud.test",
            Backend = ComfyUiBackend.Cloud,
            ApiKey = apiKey,
        };
        if (assetPageSize is not null)
        {
            options.AssetPageSize = assetPageSize.Value;
        }
        return new ComfyCloudClient(http, Options.Create(options));
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

    [Fact]
    public async Task Survives_a_transient_network_failure_while_polling_for_completion()
    {
        // A dropped connection on one poll request shouldn't lose an otherwise-successful,
        // possibly many-minutes-long generation — the client should just retry on the next tick.
        var handler = new FakeCloudServerHandler { FailNextPollAttempts = 1 };
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("z-image-turbo");
        var request = new EditRequest([], "a watercolor fox");

        var result = await client.RunWorkflowAsync(workflow, request, "a watercolor fox", null, null, CancellationToken.None);

        Assert.Equal(new byte[] { 9, 9, 9, 9 }, result.ResultBytes);
        Assert.Equal(0, handler.FailNextPollAttempts);
    }

    [Fact]
    public async Task Lists_input_and_output_assets_via_the_real_paginated_assets_api_excluding_models()
    {
        var handler = new FakeCloudServerHandler();
        var client = MakeClient(handler);

        var assets = new List<AssetInfo>();
        string? cursor = null;
        do
        {
            var page = await client.ListInputAssetsPageAsync(cursor, CancellationToken.None);
            assets.AddRange(page.Assets);
            cursor = page.NextCursor;
        } while (cursor is not null);

        // Fetched across two pages (has_more/next_cursor), following the cursor until exhausted.
        Assert.Equal([null, "page2"], handler.AssetListCursorsSeen);
        // "a.png"/"b.png" are "input"-tagged, "c.png" is "output"-tagged (a prior generation
        // result) — both belong in the browser. The page-1 "models"-tagged asset (a huge model
        // file, as real Cloud accounts mix in alongside actual images) must be filtered out.
        // loader_path is the actual fetch/download key (and the identifier), distinct from the
        // human display_name — the asset id itself is deliberately not surfaced anywhere.
        Assert.Equal(["hash-a.png", "hash-b.png", "hash-c.png"], assets.Select(a => a.Name));
    }

    [Fact]
    public async Task Only_fetches_one_server_page_per_call_not_the_whole_listing()
    {
        // A caller (e.g. a scroll-to-load-more UI) should be able to fetch just the first page
        // without the client eagerly walking every subsequent page on its own.
        var handler = new FakeCloudServerHandler();
        var client = MakeClient(handler);

        var firstPage = await client.ListInputAssetsPageAsync(null, CancellationToken.None);

        Assert.Equal([null], handler.AssetListCursorsSeen);
        Assert.Equal(["hash-a.png", "hash-b.png"], firstPage.Assets.Select(a => a.Name));
        Assert.Equal("page2", firstPage.NextCursor);

        var secondPage = await client.ListInputAssetsPageAsync(firstPage.NextCursor, CancellationToken.None);

        Assert.Equal([null, "page2"], handler.AssetListCursorsSeen);
        Assert.Equal(["hash-c.png"], secondPage.Assets.Select(a => a.Name));
        Assert.Null(secondPage.NextCursor);
    }

    [Theory]
    [InlineData(50, "50")] // within range: sent as-is
    [InlineData(1000, "500")] // above the server's hard max: clamped down
    [InlineData(0, "1")] // below the minimum: clamped up
    public async Task Sends_the_configured_asset_page_size_clamped_to_the_servers_1_to_500_range(int configured, string expectedLimit)
    {
        var handler = new FakeCloudServerHandler();
        var client = MakeClient(handler, assetPageSize: configured);

        await client.ListInputAssetsPageAsync(null, CancellationToken.None);

        Assert.Equal([expectedLimit], handler.AssetListLimitsSeen);
    }
}
