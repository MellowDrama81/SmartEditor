using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Shared orchestration for talking to a ComfyUI-compatible backend: uploads images,
/// substitutes placeholder tokens into the workflow template, submits it, waits for completion,
/// and fetches the result. Concrete subclasses (<see cref="ComfyUiClient"/> for a self-hosted
/// server, <see cref="ComfyCloudClient"/> for Comfy Cloud) implement only their backend's wire
/// dialect via the abstract hook methods below &mdash; the placeholder-substitution and
/// mask-baking logic is identical for both.</summary>
public abstract partial class ComfyUiClientBase : IComfyUiClient
{
    public virtual bool SupportsJobRecovery => false;
    private const string PromptToken = "{{PROMPT:string}}";
    private const string SeedToken = "{{SEED:seed}}";
    private const string MaskedImageToken = "{{UPLOADED_MASKED_IMAGE_FILENAME:image}}";

    public async Task<ComfyRunResult> RunWorkflowAsync(
        WorkflowDefinition workflow,
        EditRequest request,
        string refinedPrompt,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded,
        IProgress<double>? progress,
        CancellationToken ct,
        IProgress<ComfyJobState>? jobState = null,
        IProgress<ComfyJobUpdate>? jobUpdates = null)
    {
        var uploaded = new Dictionary<Guid, string>(alreadyUploaded ?? new Dictionary<Guid, string>());
        var imagesToBind = request.Images.Take(workflow.Capabilities.MaxImages).ToList();

        foreach (var image in imagesToBind)
        {
            if (!uploaded.ContainsKey(image.Id))
            {
                uploaded[image.Id] = await UploadImageAsync(image.OriginalBytes, image.FileName, ct);
            }
        }

        var template = await File.ReadAllTextAsync(workflow.GraphFilePath, ct);

        // A masked workflow either has its own dedicated upload slot for the alpha-masked image
        // (used alongside the plain original), or derives the mask straight from the primary
        // image's own alpha channel (no separate token) — detected from the template itself so
        // no extra per-workflow metadata is needed.
        string? maskedImageName = null;
        string? primaryImageOverrideName = null;
        if (workflow.Capabilities.RequiresMask && request.Mask is not null && imagesToBind.Count > 0)
        {
            var maskedBytes = MaskCompositor.ApplyMask(imagesToBind[0].OriginalBytes, request.Mask.Bytes);
            if (template.Contains(MaskedImageToken, StringComparison.Ordinal))
            {
                maskedImageName = await UploadImageAsync(maskedBytes, "masked.png", ct);
            }
            else
            {
                primaryImageOverrideName = await UploadImageAsync(maskedBytes, "masked.png", ct);
            }
        }

        // imageNames is local to this run's substitution — the returned/cached `uploaded` map
        // always keeps the *plain* upload for each source image, since a later iteration might
        // pick a different (unmasked) workflow that needs the original, not the masked, image.
        var imageNames = imagesToBind.Select(i => uploaded[i.Id]).ToList();
        if (primaryImageOverrideName is not null && imageNames.Count > 0)
        {
            imageNames[0] = primaryImageOverrideName;
        }

        var seed = Random.Shared.NextInt64(0, long.MaxValue);
        var substituted = SubstitutePlaceholders(template, refinedPrompt, seed, imageNames, maskedImageName);
        EnsureFullySubstituted(substituted, workflow.Id);

        var graph = JsonNode.Parse(substituted)
                    ?? throw new ComfyWorkflowException($"Workflow graph '{workflow.GraphFilePath}' is empty.");

        var clientId = Guid.NewGuid().ToString("N");
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = progress is null
            ? Task.CompletedTask
            : ReportProgressAsync(clientId, progress, progressCts.Token);

        string? promptId = null;
        try
        {
            promptId = await SubmitPromptAsync(graph, clientId, ct);
            jobState?.Report(ComfyJobState.Queued);
            jobUpdates?.Report(new ComfyJobUpdate(ComfyJobState.Queued, promptId));
            var outputs = await WaitForCompletionAsync(promptId, jobState, jobUpdates, ct);
            var (filename, subfolder, type) = ReadOutputImageRef(outputs, workflow.Id);
            var bytes = await FetchImageAsync(filename, subfolder, type, ct);
            var outputRef = string.IsNullOrEmpty(subfolder) ? filename : $"{subfolder}/{filename}";
            jobUpdates?.Report(new ComfyJobUpdate(ComfyJobState.Completed, promptId));
            return new ComfyRunResult(bytes, uploaded, outputRef);
        }
        catch (ComfyWorkflowException ex) when (promptId is not null && ex.IsTerminal)
        {
            jobUpdates?.Report(new ComfyJobUpdate(ComfyJobState.Failed, promptId));
            throw;
        }
        finally
        {
            progressCts.Cancel();
            try
            {
                await progressTask;
            }
            catch
            {
                // best-effort only
            }
        }
    }

    public Task<string> UploadInputAssetAsync(byte[] bytes, string fileName, CancellationToken ct) =>
        UploadImageAsync(bytes, fileName, ct);

    public Task<byte[]> DownloadInputAssetAsync(string filename, CancellationToken ct) =>
        FetchImageAsync(filename, subfolder: "", type: "input", ct);

    public virtual Task<ComfyRunResult> RecoverWorkflowAsync(string jobId, CancellationToken ct) =>
        throw new NotSupportedException("This ComfyUI backend cannot recover jobs after the app restarts.");

    /// <summary>Default (self-hosted) implementation: falls back to the <c>LoadImage</c> node's
    /// own dropdown, since there's no dedicated asset-listing API there. <see cref="ComfyCloudClient"/>
    /// overrides this with the real, paginated <c>GET /api/assets</c> endpoint instead. A
    /// self-hosted server has no pagination to page through, so <paramref name="cursor"/> is
    /// ignored and every call (there should only ever be one — the first page's
    /// <see cref="AssetPage.NextCursor"/> is always <c>null</c>) returns the full listing from a
    /// single <c>/object_info</c> fetch.</summary>
    public virtual async Task<AssetPage> ListInputAssetsPageAsync(string? cursor, CancellationToken ct)
    {
        var objectInfo = await FetchObjectInfoAsync(ct);
        var imageField = objectInfo["LoadImage"]?["input"]?["required"]?["image"]
                          ?? throw new ComfyWorkflowException(
                              "ComfyUI's object_info response has no LoadImage node/image input definition.");

        // Two shapes are seen in the wild depending on ComfyUI version: an older plain
        // [ [ "a.png", "b.png" ], {...} ] combo, or the newer ["COMBO", { "options": [...] }] shape.
        JsonArray? options = imageField switch
        {
            JsonArray { Count: > 0 } outer when outer[0] is JsonArray inner => inner,
            JsonArray { Count: > 1 } outer when outer[0]?.GetValue<string>() == "COMBO"
                                                 && outer[1] is JsonObject combo
                                                 && combo["options"] is JsonArray opts => opts,
            _ => null,
        };

        if (options is null)
        {
            throw new ComfyWorkflowException("Could not parse ComfyUI's LoadImage 'image' input options.");
        }

        // ComfyUI's own combo enum has been observed to list the same filename more than once
        // (verbatim repeats, not a SmartEditor artifact) — dedupe rather than show/re-fetch the
        // same asset multiple times. No id/display-name concept exists here, so both fall back to
        // the storage filename itself.
        var assets = options.Select(n => n!.GetValue<string>())
            .Distinct(StringComparer.Ordinal)
            .Select(name => new AssetInfo(name, name))
            .ToList();
        return new AssetPage(assets, NextCursor: null);
    }

    protected abstract Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct);

    protected abstract Task<string> SubmitPromptAsync(JsonNode graph, string clientId, CancellationToken ct);

    protected abstract Task<JsonNode> WaitForCompletionAsync(
        string promptId, IProgress<ComfyJobState>? jobState, IProgress<ComfyJobUpdate>? jobUpdates, CancellationToken ct);

    protected abstract Task<byte[]> FetchImageAsync(string filename, string subfolder, string type, CancellationToken ct);

    /// <summary>Fetches ComfyUI's full node-definition catalog. This is a genuinely large payload
    /// (multiple MB) with no lighter-weight alternative for listing already-uploaded assets, so
    /// callers (<see cref="ListInputAssetsPageAsync"/>) should call it deliberately, not on a hot path.</summary>
    protected abstract Task<JsonNode> FetchObjectInfoAsync(CancellationToken ct);

    /// <summary>Best-effort progress reporting; completion is always determined via
    /// <see cref="WaitForCompletionAsync"/>, never this. Default no-op.</summary>
    protected virtual Task ReportProgressAsync(string clientId, IProgress<double> progress, CancellationToken ct) => Task.CompletedTask;

    private static string SubstitutePlaceholders(
        string template, string prompt, long seed, IReadOnlyList<string> imageNames, string? maskedImageName)
    {
        var result = template.Replace(PromptToken, JsonEncodedText.Encode(prompt).ToString(), StringComparison.Ordinal);
        result = result.Replace(SeedToken, seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        for (var i = 0; i < imageNames.Count; i++)
        {
            var token = i == 0 ? "{{UPLOADED_IMAGE_FILENAME:image}}" : $"{{{{UPLOADED_IMAGE_FILENAME_{i + 1}:image}}}}";
            result = result.Replace(token, JsonEncodedText.Encode(imageNames[i]).ToString(), StringComparison.Ordinal);
        }

        if (maskedImageName is not null)
        {
            result = result.Replace(MaskedImageToken, JsonEncodedText.Encode(maskedImageName).ToString(), StringComparison.Ordinal);
        }

        return result;
    }

    private static void EnsureFullySubstituted(string substituted, string workflowId)
    {
        var match = PlaceholderTokenRegex().Match(substituted);
        if (match.Success)
        {
            throw new ComfyWorkflowException(
                $"Workflow '{workflowId}' still has an unresolved placeholder '{match.Value}' after substitution " +
                "(the request likely didn't supply enough images for this workflow).");
        }
    }

    [GeneratedRegex(@"\{\{[A-Z0-9_]+:\w+\}\}")]
    private static partial Regex PlaceholderTokenRegex();

    protected static (string Filename, string Subfolder, string Type) ReadOutputImageRef(JsonNode outputs, string workflowId)
    {
        foreach (var property in outputs.AsObject())
        {
            var first = property.Value?["images"]?[0];
            var filename = first?["filename"]?.GetValue<string>();
            if (string.IsNullOrEmpty(filename))
            {
                continue;
            }

            var subfolder = first?["subfolder"]?.GetValue<string>() ?? "";
            var type = first?["type"]?.GetValue<string>() ?? "output";
            return (filename, subfolder, type);
        }

        throw new ComfyWorkflowException($"Workflow '{workflowId}': no output image was produced.");
    }
}
