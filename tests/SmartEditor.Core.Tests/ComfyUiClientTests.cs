using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;
using SmartEditor.Core.Services;
using SmartEditor.Core.Tests.TestSupport;
using Xunit;

namespace SmartEditor.Core.Tests;

public class ComfyUiClientTests
{
    private static WorkflowDefinition LoadWorkflow(string id) =>
        new FileWorkflowCatalog(Path.Combine(AppContext.BaseDirectory, "Workflows"))
            .GetAll().Single(w => w.Id == id);

    private static ComfyUiClient MakeClient(FakeComfyServerHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://fake-comfy.test/") };
        return new ComfyUiClient(http, Options.Create(new ComfyUiOptions { BaseUrl = "http://fake-comfy.test" }));
    }

    private static async Task<List<AssetInfo>> ListAllAsync(IComfyUiClient client, CancellationToken ct)
    {
        var assets = new List<AssetInfo>();
        string? cursor = null;
        do
        {
            var page = await client.ListInputAssetsPageAsync(cursor, ct);
            assets.AddRange(page.Assets);
            cursor = page.NextCursor;
        } while (cursor is not null);
        return assets;
    }

    [Fact]
    public async Task Substitutes_prompt_and_seed_into_an_image_free_workflow()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("z-image-turbo");
        var request = new EditRequest([], "a watercolor fox");

        var result = await client.RunWorkflowAsync(workflow, request, "a watercolor fox in a forest", null, null, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, result.ResultBytes);
        Assert.Equal(0, handler.UploadCount);
        Assert.Contains("a watercolor fox in a forest", handler.LastPromptBody);
        Assert.DoesNotContain("{{", handler.LastPromptBody);
    }

    [Fact]
    public async Task Uploads_and_binds_a_single_reference_image()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("flux2-klein-edit-single");
        var image = new SourceImage("in.png", [1, 2, 3]);
        var request = new EditRequest([image], "make it blue");

        var result = await client.RunWorkflowAsync(workflow, request, "a vivid blue photo", null, null, CancellationToken.None);

        Assert.Equal(1, handler.UploadCount);
        Assert.Single(result.UploadedImageNames);
        Assert.Contains("a vivid blue photo", handler.LastPromptBody);
        Assert.Contains(result.UploadedImageNames[image.Id], handler.LastPromptBody);
    }

    [Fact]
    public async Task Does_not_re_upload_images_already_uploaded_in_a_prior_iteration()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("flux2-klein-edit-single");
        var image = new SourceImage("in.png", [1, 2, 3]);
        var request = new EditRequest([image], "make it blue");

        var first = await client.RunWorkflowAsync(workflow, request, "attempt 1", null, null, CancellationToken.None);
        Assert.Equal(1, handler.UploadCount);

        await client.RunWorkflowAsync(workflow, request, "attempt 2", first.UploadedImageNames, null, CancellationToken.None);

        Assert.Equal(1, handler.UploadCount);
    }

    [Fact]
    public async Task Masked_workflow_with_a_dedicated_token_uploads_plain_and_masked_versions()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("flux2-klein-inpaint-reference");
        Assert.True(workflow.Capabilities.RequiresMask);

        var image1 = new SourceImage("base.png", TestImages.TinyPng);
        var image2 = new SourceImage("ref.png", TestImages.TinyPng);
        var mask = new MaskImage(TestImages.TinyPng);
        var request = new EditRequest([image1, image2], "swap the object", mask);

        await client.RunWorkflowAsync(workflow, request, "swap the object per the reference", null, null, CancellationToken.None);

        // 2 plain reference images + 1 alpha-masked composite of image #1.
        Assert.Equal(3, handler.UploadCount);
        Assert.DoesNotContain("{{", handler.LastPromptBody);
    }

    [Fact]
    public async Task Masked_workflow_without_a_dedicated_token_bakes_the_mask_into_the_primary_upload()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("qwen-image-instantx-inpainting");
        Assert.True(workflow.Capabilities.RequiresMask);

        var image = new SourceImage("base.png", TestImages.TinyPng);
        var mask = new MaskImage(TestImages.TinyPng);
        var request = new EditRequest([image], "remove the object", mask);

        var result = await client.RunWorkflowAsync(workflow, request, "remove the object cleanly", null, null, CancellationToken.None);

        // One plain upload (cached for future non-masked iterations) + one masked-composite upload
        // actually bound into the graph.
        Assert.Equal(2, handler.UploadCount);
        Assert.DoesNotContain("{{", handler.LastPromptBody);
        Assert.DoesNotContain(result.UploadedImageNames[image.Id], handler.LastPromptBody);
    }

    [Fact]
    public async Task Throws_when_the_workflow_needs_more_images_than_supplied()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);
        var workflow = LoadWorkflow("flux2-klein-edit-double");
        var image = new SourceImage("only-one.png", [1, 2, 3]);
        var request = new EditRequest([image], "combine these");

        var ex = await Assert.ThrowsAsync<ComfyWorkflowException>(
            () => client.RunWorkflowAsync(workflow, request, "combine these", null, null, CancellationToken.None));

        Assert.Contains("unresolved placeholder", ex.Message);
    }

    [Fact]
    public async Task Lists_input_assets_from_the_LoadImage_node_options_plain_list_shape()
    {
        var handler = new FakeComfyServerHandler { ObjectInfoImageListJson = """["input-a.png","input-b.png"]""" };
        var client = MakeClient(handler);

        var assets = await ListAllAsync(client, CancellationToken.None);

        Assert.Equal(["input-a.png", "input-b.png"], assets.Select(a => a.Name));
        // No real display-name concept on self-hosted ComfyUI — it falls back to the filename.
        Assert.All(assets, a => Assert.Equal(a.Name, a.DisplayName));
    }

    [Fact]
    public async Task Lists_input_assets_deduplicated_when_ComfyUI_repeats_a_filename()
    {
        // Observed live against a real ComfyUI/Comfy Cloud object_info response: the LoadImage
        // node's own combo enum can list the same filename more than once.
        var handler = new FakeComfyServerHandler
        {
            ObjectInfoImageListJson = """["a.png","b.png","a.png","b.png","a.png"]""",
        };
        var client = MakeClient(handler);

        var assets = await ListAllAsync(client, CancellationToken.None);

        Assert.Equal(["a.png", "b.png"], assets.Select(a => a.Name));
    }

    [Fact]
    public async Task Lists_input_assets_from_the_LoadImage_node_options_combo_shape()
    {
        var handler = new FakeComfyServerHandler { ObjectInfoImageListJson = "\"COMBO\",{\"options\":[\"input-a.png\"]}" };
        var client = MakeClient(handler);

        var assets = await ListAllAsync(client, CancellationToken.None);

        Assert.Equal(["input-a.png"], assets.Select(a => a.Name));
    }

    [Fact]
    public async Task Uploads_and_downloads_an_asset_independent_of_any_workflow_run()
    {
        var handler = new FakeComfyServerHandler();
        var client = MakeClient(handler);

        var name = await client.UploadInputAssetAsync([1, 2, 3], "photo.png", CancellationToken.None);
        Assert.Equal(1, handler.UploadCount);
        Assert.False(string.IsNullOrEmpty(name));

        var bytes = await client.DownloadInputAssetAsync(name, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }
}
