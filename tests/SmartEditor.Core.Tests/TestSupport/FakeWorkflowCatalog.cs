using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Tests.TestSupport;

internal sealed class FakeWorkflowCatalog : IWorkflowCatalog
{
    private readonly IReadOnlyList<WorkflowDefinition> _workflows;

    public FakeWorkflowCatalog(params WorkflowDefinition[] workflows) => _workflows = workflows;

    public IReadOnlyList<WorkflowDefinition> GetAll() => _workflows;

    public static WorkflowDefinition SimpleWorkflow(string id = "wf1", int minImages = 1, int maxImages = 1, bool requiresMask = false) => new()
    {
        Id = id,
        DisplayName = "Test Workflow",
        Description = "A workflow used only in tests.",
        Capabilities = new WorkflowCapabilities(requiresMask, minImages, maxImages),
        GraphFilePath = "unused-in-tests.json",
    };
}
