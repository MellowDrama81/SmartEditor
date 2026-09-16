namespace SmartEditor.Core.Models;

/// <summary>Coarse-grained job states reported by a ComfyUI backend.</summary>
public enum ComfyJobState
{
    Queued,
    Generating,
    Completed,
    Failed,
}
