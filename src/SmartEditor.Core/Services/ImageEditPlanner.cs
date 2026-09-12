using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Chooses a workflow from the catalog and refines the user's prompt for it. Internal
/// collaborator of <see cref="ImageEditOrchestrator"/> &mdash; not registered in DI on its own.</summary>
internal sealed partial class ImageEditPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonNode PlanSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "workflowId": { "type": "string" },
            "refinedPrompt": { "type": "string" },
            "reasoning": { "type": "string" },
            "imageOrder": { "type": "array", "items": { "type": "integer" } }
          },
          "required": ["workflowId", "refinedPrompt", "reasoning", "imageOrder"],
          "additionalProperties": false
        }
        """)!;

    private readonly ILlmClient _llm;
    private readonly IWorkflowCatalog _catalog;
    private readonly IModelGuidanceCatalog _guidance;

    public ImageEditPlanner(ILlmClient llm, IWorkflowCatalog catalog, IModelGuidanceCatalog guidance)
    {
        _llm = llm;
        _catalog = catalog;
        _guidance = guidance;
    }

    /// <param name="forcedWorkflow">When set, the user picked this workflow directly instead of
    /// leaving the choice to the LLM &mdash; the LLM is then only asked to refine the prompt and
    /// decide the image order for it, not to choose between workflows.</param>
    /// <returns><see cref="ImageOrder"/> is a 0-based permutation of the supplied images'
    /// positions, telling <see cref="ImageEditOrchestrator"/> which original image belongs in each
    /// slot the chosen workflow expects (e.g. <c>[1, 0]</c> means "put image #2 in slot 1, image #1
    /// in slot 2") &mdash; the user may not have added images in the order a given workflow needs
    /// them (a character reference before a pose reference, say), and the LLM has already looked at
    /// the actual image content while choosing a workflow, so it's well-placed to also decide this
    /// rather than requiring the user to manually reorder them beforehand.</returns>
    public async Task<(WorkflowDefinition Workflow, string RefinedPrompt, string Reasoning, IReadOnlyList<int> ImageOrder)> PlanAsync(
        EditRequest request, EditIteration? previousIteration, WorkflowDefinition? forcedWorkflow, CancellationToken ct)
    {
        var hasMask = request.Mask is not null;
        List<WorkflowDefinition> eligibleWorkflows;

        if (forcedWorkflow is not null)
        {
            forcedWorkflow = _catalog.GetAll().FirstOrDefault(w => w.Id == forcedWorkflow.Id)
                ?? throw new InvalidOperationException("Selected workflow is disabled or deleted.");
            // Still re-checked here (not just trusted from the UI that offered it): the same
            // reasoning as the auto-selected case applies just as much to a forced one — an
            // incompatible workflow either can't be substituted into or, for a missing mask, throws
            // with no retry path at all, so it's better caught with a clear message right here.
            if (WorkflowEligibility.Filter([forcedWorkflow], request.Images.Count, hasMask).Count == 0)
            {
                throw new InvalidOperationException(
                    $"Workflow '{forcedWorkflow.Id}' does not accept {request.Images.Count} source image(s) " +
                    $"{(hasMask ? "with a mask" : "without a mask")} (it requires " +
                    $"{forcedWorkflow.Capabilities.MinImages}-{forcedWorkflow.Capabilities.MaxImages} images " +
                    $"{(forcedWorkflow.Capabilities.RequiresMask ? "with a mask" : "without a mask")}).");
            }

            eligibleWorkflows = [forcedWorkflow];
        }
        else
        {
            var workflows = _catalog.GetAll();
            if (workflows.Count == 0)
            {
                throw new InvalidOperationException("No workflows are available in the catalog.");
            }

            // Filter to workflows that actually accept the supplied image count AND match whether a
            // mask was provided, before the LLM ever sees the catalog, rather than letting it pick a
            // mismatched workflow and catching that after the fact — both are fixed for the whole
            // session, so there is nothing a retry could do to fix an out-of-range choice; better to
            // make it unchoosable. The mask check in particular isn't just a quality nicety: a
            // mask-required workflow run without a mask leaves its masked-image placeholder token
            // unresolved, which throws and kills the whole session outright (unlike a malformed LLM
            // plan or judge response, that failure mode has no retry path at all).
            eligibleWorkflows = WorkflowEligibility.Filter(workflows, request.Images.Count, hasMask).ToList();
            if (eligibleWorkflows.Count == 0)
            {
                throw new InvalidOperationException(
                    $"No workflow in the catalog accepts {request.Images.Count} source image(s) " +
                    $"{(hasMask ? "with a mask" : "without a mask")}. " +
                    $"Available workflows require between {workflows.Min(w => w.Capabilities.MinImages)} " +
                    $"and {workflows.Max(w => w.Capabilities.MaxImages)} images.");
            }
        }

        // requiresMask is deliberately not listed per-entry here: every eligible workflow now
        // shares the same value (filtered above to match hasMask exactly), so it would just be
        // noise rather than something to choose between. acceptsPrompt does vary per entry though
        // (a handful of bundled workflows are purely mechanical image operations with no use for a
        // text prompt at all), so it's called out per workflow instead.
        var catalogText = string.Join("\n", eligibleWorkflows.Select(w =>
            $"- id: {w.Id}\n  name: {w.DisplayName}\n  description: {w.Description}\n" +
            $"  images: {w.Capabilities.MinImages}-{w.Capabilities.MaxImages}" +
            (w.Capabilities.AcceptsPrompt ? "" : "\n  acceptsPrompt: false (purely mechanical — no text prompt is used)")));

        var catalogIds = eligibleWorkflows.Select(w => w.Id).ToHashSet();
        var guidanceText = string.Join("\n", _guidance.GetAll()
            .Where(g => g.WorkflowIds.Any(catalogIds.Contains))
            .Select(g =>
                $"- {g.DisplayName} (workflows: {string.Join(", ", g.WorkflowIds.Where(catalogIds.Contains))})\n" +
                $"  best for: {string.Join("; ", g.BestFor)}\n" +
                $"  struggles with: {string.Join("; ", g.StrugglesWith)}"));

        var guidanceSection = guidanceText.Length == 0
            ? ""
            : $"\n\nResearch notes on what each underlying model is actually good and bad at (use this " +
              $"to break ties and avoid a workflow's known weak spot, not just its mechanical description):\n{guidanceText}";

        var selectionInstruction = forcedWorkflow is not null
            ? $"The user has already chosen the workflow below themselves (workflowId must be exactly " +
              $"'{forcedWorkflow.Id}') — your job is only to rewrite their instructions into a detailed, " +
              $"unambiguous generation prompt suited to it, and to decide the image order below."
            : "Given the user's source images, optional mask, and instructions, choose the single best-fit " +
              "workflow from the catalog below and rewrite the user's instructions into a detailed, " +
              "unambiguous generation prompt suited to that workflow.";

        var catalogIntro = forcedWorkflow is not null
            ? "The chosen workflow:"
            : $"""
              The catalog below has already been filtered to only the workflows that accept exactly
              {request.Images.Count} source image(s) and {(hasMask ? "that use the provided mask" : "that don't require a mask")},
              so every one of them is a valid choice on both counts; choose based on fit for the task instead.

              Available workflows:
              """;

        var systemPrompt = $$"""
            You are the planning stage of an AI image-editing assistant built on ComfyUI.
            {{selectionInstruction}}

            {{catalogIntro}}
            {{catalogText}}
            {{guidanceSection}}

            The images are attached below in the order the user added them, which is not
            necessarily the order the chosen workflow's description expects them in (e.g. a
            workflow described as "image 1 = character reference, image 2 = pose guide" needs the
            actual pose image to be image 2, even if the user happened to add it first). Look at
            what each attached image actually shows and decide, for the workflow you're choosing,
            which original image belongs in each of its slots. Return that as imageOrder: a 0-based
            permutation of the attached images' positions, one entry per slot, in slot order. For
            example with 2 attached images, [0, 1] means "use them as attached" and [1, 0] means
            "swap them". If there is only one image, or the images are already in the right order,
            return the identity ordering (e.g. [0], [0, 1], [0, 1, 2], ...).

            If the workflow you're choosing has acceptsPrompt: false, it has no use for a text
            prompt at all — refinedPrompt is ignored for it, so an empty string is fine.

            Respond with ONLY a JSON object of this exact shape, no other text:
            {"workflowId": "<one of the ids above>", "refinedPrompt": "<detailed prompt>", "reasoning": "<why this workflow>", "imageOrder": [<0-based permutation, one entry per attached image>]}
            """;

        var userText = new StringBuilder();
        userText.AppendLine($"User's instructions: {request.Prompt}");
        userText.AppendLine($"Number of source images: {request.Images.Count}");
        userText.AppendLine(request.Mask is not null ? "A mask is provided on the first image." : "No mask provided.");

        if (previousIteration is not null)
        {
            userText.AppendLine();
            userText.AppendLine($"This is a retry. The previous attempt used workflow '{previousIteration.WorkflowId}' " +
                                 $"with refined prompt '{previousIteration.RefinedPrompt}'.");
            userText.AppendLine($"That result was judged unsatisfactory: {previousIteration.JudgeFeedback}");
            userText.AppendLine(forcedWorkflow is not null
                ? "The workflow is fixed by the user's own choice; adjust the refined prompt (and/or image order) to address this feedback."
                : "Adjust the workflow choice and/or refined prompt to address this feedback.");
        }

        var images = new List<byte[]>(request.Images.Select(i => i.GetLlmPreviewBytes()));
        if (request.Mask is not null)
        {
            images.Add(request.Mask.Bytes);
        }

        var llmRequest = new LlmRequest(
            systemPrompt,
            [new LlmMessage(LlmRole.User, userText.ToString(), images)],
            new JsonResponseSchema("image_edit_plan", PlanSchema));

        var raw = await _llm.CompleteAsync(llmRequest, ct);
        var dto = TolerantJson.Parse<PlanDto>(raw, JsonOptions);

        // Some providers/models are less reliable at strict JSON-schema adherence than others and
        // echo stray formatting from the prompt back into the value (observed with MiniMax M3:
        // catalog entries are rendered as "- id: <id>" and the model occasionally copies the
        // leading ": " into its answer, e.g. ":flux2-3img" instead of "flux2-3img"). Sanitize
        // before matching rather than failing on cosmetic noise the model didn't need to get right.
        // Resolved against eligibleWorkflows, not the full catalog: an id that belongs to some
        // other workflow the LLM wasn't even offered (hallucinated, or a stale id from an earlier
        // turn) is exactly as invalid as one that doesn't exist at all.
        var sanitizedId = SanitizeWorkflowId(dto.WorkflowId);
        var workflow = eligibleWorkflows.FirstOrDefault(w => w.Id == sanitizedId)
                       ?? eligibleWorkflows.FirstOrDefault(w => string.Equals(w.Id, sanitizedId, StringComparison.OrdinalIgnoreCase))
                       ?? throw new LlmResponseParseException(
                           $"LLM selected workflow id '{dto.WorkflowId}' (sanitized: '{sanitizedId}'), which is not one of " +
                           $"the workflows offered for {request.Images.Count} image(s).", raw);

        if (workflow.Capabilities.AcceptsPrompt && string.IsNullOrWhiteSpace(dto.RefinedPrompt))
        {
            throw new LlmResponseParseException(
                $"LLM returned an empty refinedPrompt for workflow '{sanitizedId}'.", raw);
        }

        var imageOrder = ValidateImageOrder(dto.ImageOrder, request.Images.Count, raw);

        return (workflow, dto.RefinedPrompt, dto.Reasoning, imageOrder);
    }

    /// <summary>A genuine permutation of every attached image's position is required if the LLM
    /// bothered to supply one at all; a missing/null value (some providers are less reliable than
    /// others at populating every declared schema field) just falls back to "as attached" rather
    /// than being treated as an error, since that's a safe, always-correct default.</summary>
    private static IReadOnlyList<int> ValidateImageOrder(int[]? imageOrder, int imageCount, string raw)
    {
        if (imageOrder is null)
        {
            return Enumerable.Range(0, imageCount).ToList();
        }

        var isValidPermutation = imageOrder.Length == imageCount
                                  && imageOrder.Distinct().Count() == imageCount
                                  && imageOrder.All(i => i >= 0 && i < imageCount);
        if (!isValidPermutation)
        {
            throw new LlmResponseParseException(
                $"LLM returned imageOrder [{string.Join(", ", imageOrder)}], which is not a valid " +
                $"permutation of the {imageCount} attached image(s) (expected each of 0-{imageCount - 1} exactly once).",
                raw);
        }

        return imageOrder;
    }

    /// <summary>Strips whitespace and any leading/trailing characters that can't legally appear in
    /// a catalog id (ids are lowercase-hyphenated, e.g. "flux2-3img"), so stray punctuation an LLM
    /// echoed from the prompt's own formatting doesn't fail an otherwise-correct match.</summary>
    private static string SanitizeWorkflowId(string rawId) =>
        WorkflowIdNoisePattern().Replace(rawId.Trim(), "");

    [GeneratedRegex(@"^[^a-zA-Z0-9]+|[^a-zA-Z0-9]+$")]
    private static partial Regex WorkflowIdNoisePattern();

    private sealed record PlanDto(string WorkflowId, string RefinedPrompt, string Reasoning, int[]? ImageOrder);
}
