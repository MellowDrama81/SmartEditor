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
}
