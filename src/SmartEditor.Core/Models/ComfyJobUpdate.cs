namespace SmartEditor.Core.Models;

/// <summary>A ComfyUI job lifecycle update. Cloud job IDs are durable and can be reconciled after
/// an Android process restart.</summary>
public sealed record ComfyJobUpdate(ComfyJobState State, string JobId);
