namespace SmartEditor.Core.Models;

public sealed record WorkflowCapabilities(bool RequiresMask, int MinImages, int MaxImages);

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
