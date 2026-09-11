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

    public string LlmBaseUrl { get; init; } = "https://openrouter.ai/api/v1";
    public string LlmApiKey { get; init; } = "";
    public string LlmModel { get; init; } = "";
    public bool LlmSupportsJsonSchema { get; init; }
    public int MaxIterations { get; init; } = 3;
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
        Directory.CreateDirectory(directory);
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
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Current = settings;
        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}
