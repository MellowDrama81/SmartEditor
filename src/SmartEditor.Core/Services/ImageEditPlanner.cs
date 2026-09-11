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
            "reasoning": { "type": "string" }
          },
          "required": ["workflowId", "refinedPrompt", "reasoning"],
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

    public async Task<(WorkflowDefinition Workflow, string RefinedPrompt, string Reasoning)> PlanAsync(
        EditRequest request, EditIteration? previousIteration, CancellationToken ct)
    {
        var workflows = _catalog.GetAll();
        if (workflows.Count == 0)
        {
            throw new InvalidOperationException("No workflows are available in the catalog.");
        }

        // Filter to workflows that actually accept the supplied image count before the LLM ever
        // sees the catalog, rather than letting it pick a mismatched workflow and catching that
        // after the fact — the image count is fixed for the whole session, so there is nothing a
        // retry could do to fix an out-of-range choice; better to make it unchoosable.
        var eligibleWorkflows = workflows
            .Where(w => request.Images.Count >= w.Capabilities.MinImages && request.Images.Count <= w.Capabilities.MaxImages)
            .ToList();
        if (eligibleWorkflows.Count == 0)
        {
            throw new InvalidOperationException(
                $"No workflow in the catalog accepts {request.Images.Count} source image(s). " +
                $"Available workflows require between {workflows.Min(w => w.Capabilities.MinImages)} " +
                $"and {workflows.Max(w => w.Capabilities.MaxImages)} images.");
        }

        var catalogText = string.Join("\n", eligibleWorkflows.Select(w =>
            $"- id: {w.Id}\n  name: {w.DisplayName}\n  description: {w.Description}\n" +
            $"  requiresMask: {w.Capabilities.RequiresMask}\n  images: {w.Capabilities.MinImages}-{w.Capabilities.MaxImages}"));

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

        var systemPrompt = $$"""
            You are the planning stage of an AI image-editing assistant built on ComfyUI.
            Given the user's source images, optional mask, and instructions, choose the single best-fit
            workflow from the catalog below and rewrite the user's instructions into a detailed,
            unambiguous generation prompt suited to that workflow.

            The catalog below has already been filtered to only the workflows that accept exactly
            {{request.Images.Count}} source image(s), so every one of them is a valid choice on that
            count; choose based on fit for the task instead.

            Available workflows:
            {{catalogText}}
            {{guidanceSection}}

            Respond with ONLY a JSON object of this exact shape, no other text:
            {"workflowId": "<one of the ids above>", "refinedPrompt": "<detailed prompt>", "reasoning": "<why this workflow>"}
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
            userText.AppendLine("Adjust the workflow choice and/or refined prompt to address this feedback.");
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

        if (string.IsNullOrWhiteSpace(dto.RefinedPrompt))
        {
            throw new LlmResponseParseException(
                $"LLM returned an empty refinedPrompt for workflow '{sanitizedId}'.", raw);
        }

        return (workflow, dto.RefinedPrompt, dto.Reasoning);
    }

    /// <summary>Strips whitespace and any leading/trailing characters that can't legally appear in
    /// a catalog id (ids are lowercase-hyphenated, e.g. "flux2-3img"), so stray punctuation an LLM
    /// echoed from the prompt's own formatting doesn't fail an otherwise-correct match.</summary>
    private static string SanitizeWorkflowId(string rawId) =>
        WorkflowIdNoisePattern().Replace(rawId.Trim(), "");

    [GeneratedRegex(@"^[^a-zA-Z0-9]+|[^a-zA-Z0-9]+$")]
    private static partial Regex WorkflowIdNoisePattern();

    private sealed record PlanDto(string WorkflowId, string RefinedPrompt, string Reasoning);
}
