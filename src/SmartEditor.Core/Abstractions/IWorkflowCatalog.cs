using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Loads the bundled set of ComfyUI workflows the LLM can choose between. Implementations
/// validate the whole catalog eagerly (at construction) so a broken workflow file is a startup
/// error, not a mid-session surprise.</summary>
public interface IWorkflowCatalog
{
    IReadOnlyList<WorkflowDefinition> GetAll();
}
