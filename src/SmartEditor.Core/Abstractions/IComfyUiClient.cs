using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Thrown when ComfyUI rejects the submitted graph (bad node/slot binding) or reports an
/// execution error. This indicates a workflow/binding bug, not a "the LLM should try again" case.</summary>
public sealed class ComfyWorkflowException : Exception
{
    public bool IsTerminal { get; }

    public ComfyWorkflowException(string message, bool isTerminal = false) : base(message) => IsTerminal = isTerminal;
    public ComfyWorkflowException(string message, Exception inner, bool isTerminal = false) : base(message, inner) => IsTerminal = isTerminal;
}

/// <param name="OutputFilename">The result image's own storage identity on the backend (as
/// produced by the workflow run itself, tagged "output" there) &mdash; <c>subfolder/filename</c>
/// when a subfolder was reported, otherwise just <c>filename</c>. Comfy Cloud's asset listing
/// (<see cref="IComfyUiClient.ListInputAssetsPageAsync"/>) surfaces this same asset again as its
/// own browsable entry, distinct from any separate copy a caller might upload elsewhere (e.g. to
/// make the result reusable as a new source image) &mdash; callers that do both can use this to
/// recognize the two as the same underlying result and avoid showing it twice.</param>
public sealed record ComfyRunResult(
    byte[] ResultBytes,
    IReadOnlyDictionary<Guid, string> UploadedImageNames,
    string OutputFilename);

/// <summary>One page of <see cref="IComfyUiClient.ListInputAssetsPageAsync"/>. <see cref="NextCursor"/>
/// is <c>null</c> once there is nothing more to fetch (pass it back in to get the following page,
/// or a caller that just wants the first page passes <c>null</c> in).</summary>
public sealed record AssetPage(IReadOnlyList<AssetInfo> Assets, string? NextCursor);

/// <summary>Talks to a ComfyUI server: uploads images, submits a workflow graph with values bound
/// into it, waits for completion, and fetches the resulting image.</summary>
public interface IComfyUiClient
{
    /// <summary>Whether this backend exposes durable job IDs that can be reconciled after the app
    /// process restarts. Currently only Comfy Cloud supports this.</summary>
    bool SupportsJobRecovery { get; }
    /// <param name="alreadyUploaded">Source-image-id &#8594; server-side filename map from a prior
    /// iteration in the same session, so unchanged source images aren't re-uploaded.</param>
    /// <param name="progress">Optional 0.0-1.0 progress reporter (best-effort, via ComfyUI's
    /// WebSocket feed &mdash; never relied on for completion detection).</param>
    /// <param name="jobState">Optional coarse job-state reporter. Comfy Cloud reports queued and
    /// running states while polling; self-hosted ComfyUI only reports that submission succeeded.</param>
    Task<ComfyRunResult> RunWorkflowAsync(
        WorkflowDefinition workflow,
        EditRequest request,
        string refinedPrompt,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded,
        IProgress<double>? progress,
        CancellationToken ct,
        IProgress<ComfyJobState>? jobState = null,
        IProgress<ComfyJobUpdate>? jobUpdates = null);

    /// <summary>Waits for an already-submitted Comfy Cloud job and returns its output. Self-hosted
    /// ComfyUI cannot reliably recover jobs after an app restart and returns an unsupported error.</summary>
    Task<ComfyRunResult> RecoverWorkflowAsync(string jobId, CancellationToken ct);

    /// <summary>Fetches one page of every browsable image currently sitting on the backend &mdash;
    /// both uploaded source images (the <c>input</c> store) and prior generation results
    /// (<c>output</c>). Pass <c>cursor: null</c> for the first page, then feed each page's own
    /// <see cref="AssetPage.NextCursor"/> back in to advance; <c>NextCursor</c> is <c>null</c> once
    /// there's nothing left, so a caller (e.g. a scroll-to-load-more UI) only ever fetches as much
    /// as it actually needs to show, instead of the whole listing up front &mdash; on Comfy Cloud an
    /// account can have thousands of assets. On Comfy Cloud this reads the real, paginated
    /// <c>GET /api/assets</c> endpoint (ids, display names, and more), filtered to
    /// "input"/"output"-tagged entries only &mdash; that endpoint also returns installed model
    /// weights (checkpoints/LoRAs/etc, individually up to tens of GB), which must never be listed
    /// or thumbnailed here. Self-hosted ComfyUI has no equivalent listing API and no concept of
    /// tags or cursors &mdash; there, this falls back to the filenames listed in the
    /// <c>LoadImage</c> node's own dropdown (<c>/object_info</c>, a single non-paginated fetch that
    /// only ever covers <c>input</c> images with no id/display name distinct from the storage
    /// filename), always returned as one page with a <c>null</c> <see cref="AssetPage.NextCursor"/>.</summary>
    Task<AssetPage> ListInputAssetsPageAsync(string? cursor, CancellationToken ct);

    /// <summary>Downloads one asset's raw bytes from the <c>input</c> store by filename (as
    /// returned by <see cref="ListInputAssetsPageAsync"/> or <see cref="UploadInputAssetAsync"/>).</summary>
    Task<byte[]> DownloadInputAssetAsync(string filename, CancellationToken ct);

    /// <summary>Uploads a new image into ComfyUI's <c>input</c> asset store and returns the
    /// server-assigned filename, independent of any workflow run.</summary>
    Task<string> UploadInputAssetAsync(byte[] bytes, string fileName, CancellationToken ct);
}
