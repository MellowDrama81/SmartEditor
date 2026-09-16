namespace SmartEditor.Core.Models;

/// <summary>Describes the currently active stage of an LLM-guided edit run.</summary>
public sealed record EditRunProgress(EditRunStage Stage, int Iteration, double? Completion = null);

public enum EditRunStage
{
    Planning,
    Queued,
    Generating,
    Judging,
}
