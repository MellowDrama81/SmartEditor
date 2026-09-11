using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Runs the full plan &#8594; execute &#8594; judge loop, retrying (bounded) until the LLM
/// judges the result satisfactory or attempts are exhausted.</summary>
public interface IImageEditOrchestrator
{
    Task<EditSession> RunAsync(EditRequest request, IProgress<EditIteration>? progress, CancellationToken ct);
}
