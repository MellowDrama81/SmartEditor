namespace SmartEditor.Core.Models;

/// <summary>One image in a ComfyUI backend's <c>input</c> asset store.</summary>
/// <param name="Name">The storage filename &mdash; the actual reference used to bind this asset
/// into a <c>LoadImage</c> node or fetch it via <see cref="Abstractions.IComfyUiClient.DownloadInputAssetAsync"/>.
/// Not necessarily human-readable (Comfy typically stores uploads under a content-hashed
/// filename). This is also the identifier, since it's the one value that actually refers to the
/// asset anywhere in ComfyUI &mdash; there's no separate asset id used for that purpose.</param>
/// <param name="DisplayName">The human-facing name (the original upload filename, or a job's
/// output filename) &mdash; on self-hosted ComfyUI this is just <see cref="Name"/> again, since
/// there's no separate display-name concept there.</param>
public sealed record AssetInfo(string Name, string DisplayName);
