namespace SmartEditor.Core.Models;

public sealed class EditIteration
{
    public required int Index { get; init; }
    public required string WorkflowId { get; init; }
    public required string RefinedPrompt { get; init; }
    public required string PlannerReasoning { get; init; }
    /// <summary>Null when the workflow was never actually run (e.g. an image-count mismatch was
    /// caught before submission) rather than run and judged unsatisfactory.</summary>
    public byte[]? ResultImageBytes { get; init; }
    public required bool Satisfied { get; init; }
    public required string JudgeFeedback { get; init; }
}

public enum EditSessionStatus
{
    Running,
    Succeeded,
    ExhaustedAttempts,
    Failed,
}

public sealed class EditSession
{
    public required EditRequest Request { get; init; }
    public List<EditIteration> History { get; } = [];
    public EditSessionStatus Status { get; set; } = EditSessionStatus.Running;
    public byte[]? FinalResultBytes { get; set; }
    public string? FailureReason { get; set; }
}
