using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Thrown when ComfyUI rejects the submitted graph (bad node/slot binding) or reports an
/// execution error. This indicates a workflow/binding bug, not a "the LLM should try again" case.</summary>
public sealed class ComfyWorkflowException : Exception
{
    public ComfyWorkflowException(string message) : base(message) { }
    public ComfyWorkflowException(string message, Exception inner) : base(message, inner) { }
}

public sealed record ComfyRunResult(
    byte[] ResultBytes,
    IReadOnlyDictionary<Guid, string> UploadedImageNames);

/// <summary>Talks to a ComfyUI server: uploads images, submits a workflow graph with values bound
/// into it, waits for completion, and fetches the resulting image.</summary>
public interface IComfyUiClient
{
    /// <param name="alreadyUploaded">Source-image-id &#8594; server-side filename map from a prior
    /// iteration in the same session, so unchanged source images aren't re-uploaded.</param>
    /// <param name="progress">Optional 0.0-1.0 progress reporter (best-effort, via ComfyUI's
    /// WebSocket feed &mdash; never relied on for completion detection).</param>
    Task<ComfyRunResult> RunWorkflowAsync(
        WorkflowDefinition workflow,
        EditRequest request,
        string refinedPrompt,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded,
        IProgress<double>? progress,
        CancellationToken ct);
}
