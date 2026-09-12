using SmartEditor.Core.Services;
using Xunit;

namespace SmartEditor.Core.Tests;

public class FileWorkflowCatalogTests
{
    private static string WorkflowsDir => Path.Combine(AppContext.BaseDirectory, "Workflows");

    [Fact]
    public void Loads_the_bundled_workflows()
    {
        var catalog = new FileWorkflowCatalog(WorkflowsDir);
        var workflows = catalog.GetAll();

        Assert.Equal(68, workflows.Count);
        Assert.Single(workflows, w => w.Id == "qwen-fun-union-controlnet");
        foreach (var id in new[]
                 {
                     "qwen-canny-union-controlnet", "qwen-depth-union-controlnet",
                     "qwen-lineart-union-controlnet", "qwen-normal-union-controlnet",
                     "upscale-with-model", "remove-background", "flux2-inpaint",
                     "qwen-diffsynth-canny-controlnet", "qwen-diffsynth-depth-controlnet", "qwen-diffsynth-inpaint",
                     "api-krea2-style-reference-1img", "api-krea2-style-reference-2img", "api-krea2-style-reference-3img",
                 })
        {
            Assert.Single(workflows, w => w.Id == id);
        }

        // Multi-reference variants added after live Comfy Cloud verification.
        foreach (var n in new[] { 2, 3, 4, 5, 6, 7, 8 })
        {
            var seedream = Assert.Single(workflows, w => w.Id == $"api-bytedance-seedream4-{n}img");
            Assert.Equal(n, seedream.Capabilities.MinImages);
            Assert.Equal(n, seedream.Capabilities.MaxImages);
        }
        foreach (var n in new[] { 0, 1, 3, 4, 5, 6, 7, 8 })
        {
            Assert.Single(workflows, w => w.Id == $"api-nano-banana-pro-{n}img");
        }
        foreach (var n in new[] { 2, 3 })
        {
            var krea = Assert.Single(workflows, w => w.Id == $"krea2-style-reference-{n}img");
            Assert.Equal(n, krea.Capabilities.MinImages);
            Assert.Equal(n, krea.Capabilities.MaxImages);
        }
        foreach (var n in new[] { 1, 3 })
        {
            var qwen = Assert.Single(workflows, w => w.Id == $"qwen-image-edit-2511-{n}img");
            Assert.Equal(n, qwen.Capabilities.MinImages);
            Assert.Equal(n, qwen.Capabilities.MaxImages);
        }
        foreach (var n in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
        {
            var flux = Assert.Single(workflows, w => w.Id == $"flux2-{n}img");
            Assert.Equal(n, flux.Capabilities.MinImages);
            Assert.Equal(n, flux.Capabilities.MaxImages);
        }
        foreach (var n in new[] { 0, 3, 4, 5, 6, 7, 8 })
        {
            var klein = Assert.Single(workflows, w => w.Id == $"flux2-klein-{n}img");
            Assert.Equal(n, klein.Capabilities.MinImages);
            Assert.Equal(n, klein.Capabilities.MaxImages);
        }

        var zImageTurbo = Assert.Single(workflows, w => w.Id == "z-image-turbo");
        Assert.Equal(0, zImageTurbo.Capabilities.MinImages);
        Assert.Equal(0, zImageTurbo.Capabilities.MaxImages);
        Assert.False(zImageTurbo.Capabilities.RequiresMask);

        var maskedWorkflows = workflows.Where(w => w.Capabilities.RequiresMask).Select(w => w.Id).OrderBy(id => id).ToArray();
        Assert.Equal(
            new[] { "flux2-inpaint", "flux2-klein-inpaint-reference", "qwen-diffsynth-inpaint", "qwen-image-edit-2511-inpainting", "qwen-image-instantx-inpainting" },
            maskedWorkflows);

        var promptFreeWorkflows = workflows.Where(w => !w.Capabilities.AcceptsPrompt).Select(w => w.Id).OrderBy(id => id).ToArray();
        Assert.Equal(
            new[]
            {
                "canny-extract-control-guide", "depth-extract-control-guide", "dwpose-extract-pose-guide",
                "lineart-extract-control-guide", "normal-extract-control-guide", "remove-background", "upscale-with-model",
            },
            promptFreeWorkflows);

        // Three metadata entries intentionally share the same underlying graph template.
        var sharedGraphIds = new[] { "qwen-image-edit-2511", "qwen-image-edit-2511-character-pose", "qwen-image-edit-2511-character-prepared-pose" };
        var graphPaths = workflows.Where(w => sharedGraphIds.Contains(w.Id)).Select(w => w.GraphFilePath).Distinct().ToArray();
        Assert.Single(graphPaths);
    }

    [Fact]
    public void Rejects_a_graph_file_that_is_not_valid_json()
    {
        var tempDir = Directory.CreateTempSubdirectory("smarteditor-catalog-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "broken.json"), "{ this is not valid json ");
            File.WriteAllText(Path.Combine(tempDir, "broken.meta.json"), """
                {
                  "id": "broken",
                  "displayName": "Broken",
                  "description": "Has a malformed graph file.",
                  "graphFile": "broken.json",
                  "capabilities": { "requiresMask": false, "minImages": 0, "maxImages": 1 }
                }
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => new FileWorkflowCatalog(tempDir));
            Assert.Contains("broken.json", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Rejects_a_meta_file_referencing_a_missing_graph_file()
    {
        var tempDir = Directory.CreateTempSubdirectory("smarteditor-catalog-test").FullName;
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "broken.meta.json"), """
                {
                  "id": "broken",
                  "displayName": "Broken",
                  "description": "References a graph file that doesn't exist.",
                  "graphFile": "does-not-exist.json",
                  "capabilities": { "requiresMask": false, "minImages": 0, "maxImages": 1 }
                }
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => new FileWorkflowCatalog(tempDir));
            Assert.Contains("does-not-exist.json", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
