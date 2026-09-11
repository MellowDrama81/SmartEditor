using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Runs the full plan &#8594; execute &#8594; judge loop, retrying (bounded) until the LLM
/// judges the result satisfactory or attempts are exhausted.</summary>
public interface IImageEditOrchestrator
{
    /// <param name="alreadyUploaded">Source-image-id &#8594; server-side filename map for images
    /// the caller already knows are sitting on the Comfy backend (e.g. uploaded immediately when
    /// added to the editor, or picked from the asset library in the first place) &mdash; seeds the
    /// same dedupe mechanism normally built up iteration-to-iteration within one run, so a run's
    /// very first iteration doesn't re-upload something that's already there.</param>
    Task<EditSession> RunAsync(
        EditRequest request,
        IProgress<EditIteration>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded = null);
}
