using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

/// <summary>One entry in an editor tab's workflow picker: either a real <see cref="Workflow"/> the
/// user is pinning the run to, or the "let the LLM decide" sentinel (<see cref="Workflow"/> is
/// <c>null</c>) that's always first and selected by default.</summary>
public sealed class WorkflowOptionViewModel
{
    public WorkflowDefinition? Workflow { get; }
    public string DisplayName { get; }
    public string? Description => Workflow?.Description;

    public WorkflowOptionViewModel(WorkflowDefinition? workflow)
    {
        Workflow = workflow;
        DisplayName = workflow?.DisplayName ?? "Let the LLM decide";
    }
}
