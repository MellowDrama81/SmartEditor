namespace SmartEditor.Core.Models;

/// <param name="AcceptsPrompt">Whether this workflow's graph actually has a
/// <c>{{PROMPT:string}}</c> placeholder to substitute a text prompt into. A handful of bundled
/// workflows are purely mechanical image operations (remove-background, upscale-with-model, the
/// ControlNet guide extractors) with nothing for a text prompt to describe &mdash; derived
/// automatically from the graph file itself (see <see cref="Services.FileWorkflowCatalog"/>)
/// rather than hand-authored, so it can never drift from what the graph actually contains.</param>
public sealed record WorkflowCapabilities(bool RequiresMask, int MinImages, int MaxImages, bool AcceptsPrompt = true);

/// <summary>Describes one bundled ComfyUI workflow: what it's good for, its image/mask
/// requirements, and where its graph template lives. The graph itself carries placeholder tokens
/// (<c>{{PROMPT:string}}</c>, <c>{{SEED:seed}}</c>, <c>{{UPLOADED_IMAGE_FILENAME:image}}</c>/<c>_2</c>/<c>_3</c>,
/// <c>{{UPLOADED_MASKED_IMAGE_FILENAME:image}}</c>) substituted at submission time &mdash; see
/// <see cref="SmartEditor.Core.Services.ComfyUiClient"/> &mdash; so no node-id metadata is needed here.</summary>
public sealed class WorkflowDefinition
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required WorkflowCapabilities Capabilities { get; init; }
    public required string GraphFilePath { get; init; }
}
