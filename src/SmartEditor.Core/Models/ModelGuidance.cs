namespace SmartEditor.Core.Models;

/// <summary>Research-backed notes on what one underlying generative model (or preprocessing
/// family) is good at and where it struggles, independent of any single workflow's wiring. Used
/// to give the planning LLM real signal beyond a workflow's mechanical description &mdash; see
/// <see cref="SmartEditor.Core.Abstractions.IModelGuidanceCatalog"/> and
/// <c>docs/model-capabilities.md</c> for the human-readable version with sources.</summary>
public sealed class ModelGuidance
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>The catalog workflow ids backed by this model. Every id in the bundled workflow
    /// catalog is expected to appear in exactly one <see cref="ModelGuidance"/> entry's list.</summary>
    public required IReadOnlyList<string> WorkflowIds { get; init; }

    public required IReadOnlyList<string> BestFor { get; init; }
    public required IReadOnlyList<string> StrugglesWith { get; init; }
}
