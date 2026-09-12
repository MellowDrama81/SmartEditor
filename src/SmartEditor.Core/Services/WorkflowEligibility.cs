using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>The single rule for whether a workflow can be used for a given request: it must
/// accept the supplied image count, and its mask requirement must match whether a mask was
/// provided. Shared between <see cref="ImageEditPlanner"/> (filtering what the LLM is offered to
/// choose from) and any UI that lets a user pick a workflow directly, so both apply exactly the
/// same rule rather than risking two independently-maintained copies drifting apart.</summary>
public static class WorkflowEligibility
{
    public static IReadOnlyList<WorkflowDefinition> Filter(IEnumerable<WorkflowDefinition> workflows, int imageCount, bool hasMask) =>
        workflows
            .Where(w => imageCount >= w.Capabilities.MinImages && imageCount <= w.Capabilities.MaxImages
                        && w.Capabilities.RequiresMask == hasMask)
            .ToList();
}
