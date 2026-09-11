namespace SmartEditor.Core.Configuration;

public enum ComfyUiBackend
{
    /// <summary>A plain self-hosted ComfyUI server (unprefixed <c>/prompt</c>, <c>/history</c>,
    /// <c>/upload/image</c>, <c>/view</c> endpoints, no auth).</summary>
    SelfHosted,

    /// <summary>Comfy Cloud (<c>cloud.comfy.org</c>) &mdash; every endpoint lives under an
    /// <c>/api/</c> prefix, uses <c>X-API-Key</c> auth, and has a different job-status/output
    /// shape from self-hosted ComfyUI. See <see cref="SmartEditor.Core.Services.ComfyCloudClient"/>.</summary>
    Cloud,
}

public sealed class ComfyUiOptions
{
    public const string SectionName = "ComfyUi";

    /// <summary>Base URL of the ComfyUI server, e.g. http://192.168.1.50:8188 (self-hosted) or
    /// https://cloud.comfy.org (Cloud).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    public ComfyUiBackend Backend { get; set; } = ComfyUiBackend.SelfHosted;

    /// <summary>Comfy Cloud API key. Only used when <see cref="Backend"/> is <see cref="ComfyUiBackend.Cloud"/>.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>How many assets to request per page from Comfy Cloud's <c>GET /api/assets</c>
    /// (the <c>limit</c> query param) &mdash; only used when <see cref="Backend"/> is
    /// <see cref="ComfyUiBackend.Cloud"/>; self-hosted ComfyUI has no paginated listing to size.
    /// The server hard-rejects (<c>INVALID_LIMIT</c>) anything over 500, confirmed live, so
    /// <see cref="Services.ComfyCloudClient"/> clamps to [1, 500] regardless of what's configured
    /// here. A smaller page means more, smaller requests as the user scrolls through the asset
    /// browser; a larger page means fewer, heavier ones.</summary>
    public int AssetPageSize { get; set; } = 500;
}
