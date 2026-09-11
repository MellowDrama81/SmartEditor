using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Chooses a workflow from the catalog and refines the user's prompt for it. Internal
/// collaborator of <see cref="ImageEditOrchestrator"/> &mdash; not registered in DI on its own.</summary>
internal sealed class ImageEditPlanner
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

        var catalogText = string.Join("\n", workflows.Select(w =>
            $"- id: {w.Id}\n  name: {w.DisplayName}\n  description: {w.Description}\n" +
            $"  requiresMask: {w.Capabilities.RequiresMask}\n  images: {w.Capabilities.MinImages}-{w.Capabilities.MaxImages}"));

        var catalogIds = workflows.Select(w => w.Id).ToHashSet();
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

        var workflow = workflows.FirstOrDefault(w => w.Id == dto.WorkflowId)
                       ?? throw new LlmResponseParseException($"LLM selected unknown workflow id '{dto.WorkflowId}'.", raw);

        return (workflow, dto.RefinedPrompt, dto.Reasoning);
    }

    private sealed record PlanDto(string WorkflowId, string RefinedPrompt, string Reasoning);
}
