using System.Text.Json;
using System.Text.Json.Serialization;
using SmartEditor.Core.Configuration;

namespace SmartEditor.App.Services;

/// <summary>The user-editable settings that drive workflow selection (max iterations), ComfyUI
/// connectivity, and LLM provider access. Persisted as plain JSON locally &mdash; API keys are
/// stored unencrypted, which is acceptable for a local/dev tool but is called out in the Settings
/// UI rather than done silently.</summary>
public sealed record AppSettings
{
    public ComfyUiBackend ComfyUiBackend { get; init; } = ComfyUiBackend.SelfHosted;
    public string ComfyUiBaseUrl { get; init; } = "http://127.0.0.1:8188";

    /// <summary>Only used when <see cref="ComfyUiBackend"/> is <see cref="Configuration.ComfyUiBackend.Cloud"/>.</summary>
    public string ComfyCloudApiKey { get; init; } = "";

    /// <summary>How many assets to fetch per page (Comfy Cloud only — self-hosted ComfyUI has no
    /// paginated asset listing). Clamped to [1, 500] by <see cref="Core.Services.ComfyCloudClient"/>,
    /// the server's own hard maximum.</summary>
    public int AssetPageSize { get; init; } = 500;

    /// <summary>Maximum number of asset thumbnails stored locally. Set to zero to turn off
    /// thumbnail caching.</summary>
    public int ThumbnailCacheLimit { get; init; } = 200;

    /// <summary>Maximum number of full-resolution asset images stored locally. Set to zero to
    /// turn off full-image caching.</summary>
    public int FullImageCacheLimit { get; init; } = 40;

    public string LlmBaseUrl { get; init; } = "https://openrouter.ai/api/v1";
    public string LlmApiKey { get; init; } = "";
    public string LlmModel { get; init; } = "";
    public bool LlmSupportsJsonSchema { get; init; }
    public int MaxIterations { get; init; } = 3;

    /// <summary>Whether an LLM provider is actually usable &mdash; the base URL alone always has a
    /// default, so an API key and a model id are what actually distinguish "configured" from not.
    /// Gates whether "let the LLM decide" and prompt refinement are offered at all; without this,
    /// editor tabs fall back to Comfy-only generation against a pinned workflow.</summary>
    public bool IsLlmConfigured => !string.IsNullOrWhiteSpace(LlmApiKey) && !string.IsNullOrWhiteSpace(LlmModel);
}

/// <summary>Loads/saves <see cref="AppSettings"/> to a local JSON file under the platform's
/// application-data folder. Kept as an in-memory singleton so a Settings change is immediately
/// visible to the next edit run, without needing an app restart or a reloadable DI container.</summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _filePath;

    public AppSettings Current { get; private set; }

    public AppSettingsStore()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SmartEditor");
        _filePath = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    private AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Persists settings and only publishes them to the running app after the write succeeds.</summary>
    public bool Save(AppSettings settings)
    {
        if (!AtomicFile.TryWriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions)))
        {
            return false;
        }

        Current = settings;
        return true;
    }
}
