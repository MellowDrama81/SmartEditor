using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.App.Services;
using SmartEditor.Core.Configuration;

namespace SmartEditor.App.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private const string DefaultSelfHostedUrl = "http://127.0.0.1:8188";
    private const string DefaultCloudUrl = "https://cloud.comfy.org";

    private readonly AppSettingsStore _store;
    private readonly AssetThumbnailCache _imageCache;

    [ObservableProperty]
    public partial bool IsComfyCloud { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleartextWarning))]
    [NotifyPropertyChangedFor(nameof(HasCleartextWarning))]
    public partial string ComfyUiBaseUrl { get; set; }

    /// <summary>Only used when <see cref="IsComfyCloud"/> is set.</summary>
    [ObservableProperty]
    public partial string ComfyCloudApiKey { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CleartextWarning))]
    [NotifyPropertyChangedFor(nameof(HasCleartextWarning))]
    public partial string LlmBaseUrl { get; set; }

    [ObservableProperty]
    public partial string LlmApiKey { get; set; }

    [ObservableProperty]
    public partial string LlmModel { get; set; }

    [ObservableProperty]
    public partial bool LlmSupportsJsonSchema { get; set; }

    [ObservableProperty]
    public partial int MaxIterations { get; set; }

    /// <summary>Only used when <see cref="IsComfyCloud"/> is set — self-hosted ComfyUI has no
    /// paginated asset listing.</summary>
    [ObservableProperty]
    public partial int AssetPageSize { get; set; }

    [ObservableProperty]
    public partial int ThumbnailCacheLimit { get; set; }

    [ObservableProperty]
    public partial int FullImageCacheLimit { get; set; }

    public string CleartextWarning =>
        IsCleartext(ComfyUiBaseUrl) || IsCleartext(LlmBaseUrl)
            ? "One or more URLs use plain http:// — on Android this requires allowing cleartext traffic, which this app permits by default since these hosts are set at runtime."
            : "";

    public bool HasCleartextWarning => CleartextWarning.Length > 0;

    public SettingsViewModel(AppSettingsStore store, AssetThumbnailCache imageCache)
    {
        _store = store;
        _imageCache = imageCache;
        var current = store.Current;
        IsComfyCloud = current.ComfyUiBackend == ComfyUiBackend.Cloud;
        ComfyUiBaseUrl = current.ComfyUiBaseUrl;
        ComfyCloudApiKey = current.ComfyCloudApiKey;
        LlmBaseUrl = current.LlmBaseUrl;
        LlmApiKey = current.LlmApiKey;
        LlmModel = current.LlmModel;
        LlmSupportsJsonSchema = current.LlmSupportsJsonSchema;
        MaxIterations = current.MaxIterations;
        AssetPageSize = current.AssetPageSize;
        ThumbnailCacheLimit = current.ThumbnailCacheLimit;
        FullImageCacheLimit = current.FullImageCacheLimit;
    }

    // Swap in a sensible default URL when toggling backends, unless the user already typed
    // something else in.
    partial void OnIsComfyCloudChanged(bool value)
    {
        if (value && (string.IsNullOrWhiteSpace(ComfyUiBaseUrl) || ComfyUiBaseUrl == DefaultSelfHostedUrl))
        {
            ComfyUiBaseUrl = DefaultCloudUrl;
        }
        else if (!value && (string.IsNullOrWhiteSpace(ComfyUiBaseUrl) || ComfyUiBaseUrl == DefaultCloudUrl))
        {
            ComfyUiBaseUrl = DefaultSelfHostedUrl;
        }
    }

    private static bool IsCleartext(string url) => url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    public async Task SaveAsync()
    {
        _store.Save(new AppSettings
        {
            ComfyUiBackend = IsComfyCloud ? ComfyUiBackend.Cloud : ComfyUiBackend.SelfHosted,
            ComfyUiBaseUrl = ComfyUiBaseUrl,
            ComfyCloudApiKey = ComfyCloudApiKey,
            LlmBaseUrl = LlmBaseUrl,
            LlmApiKey = LlmApiKey,
            LlmModel = LlmModel,
            LlmSupportsJsonSchema = LlmSupportsJsonSchema,
            MaxIterations = Math.Clamp(MaxIterations, 1, OrchestratorOptions.HardMaxIterations),
            AssetPageSize = Math.Clamp(AssetPageSize, 1, 500),
            ThumbnailCacheLimit = Math.Clamp(ThumbnailCacheLimit, 0, 1_000),
            FullImageCacheLimit = Math.Clamp(FullImageCacheLimit, 0, 1_000),
        });
        await _imageCache.ApplyLimitsAsync();
    }
}
