using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Services;

namespace SmartEditor.App.Services;

/// <summary>Builds a fresh <see cref="IImageEditOrchestrator"/> (and the LLM/ComfyUI clients it
/// needs) from whatever settings are currently saved, every time one is requested. This sidesteps
/// options hot-reload machinery entirely: a Settings change takes effect on the very next run
/// simply because the next run builds new client instances from <see cref="AppSettingsStore.Current"/>.</summary>
public interface IEditSessionFactory
{
    IImageEditOrchestrator CreateOrchestrator();
}

public sealed class EditSessionFactory : IEditSessionFactory
{
    /// <summary>Named client for Comfy Cloud, registered with automatic redirect-following
    /// disabled so <see cref="ComfyCloudClient"/> can follow the <c>/api/view</c> redirect to a
    /// signed third-party URL manually, without forwarding the Cloud API key to that host.</summary>
    public const string ComfyCloudHttpClientName = "ComfyCloud";

    private readonly AppSettingsStore _settingsStore;
    private readonly IWorkflowCatalog _catalog;
    private readonly IModelGuidanceCatalog _guidance;
    private readonly IHttpClientFactory _httpClientFactory;

    public EditSessionFactory(
        AppSettingsStore settingsStore, IWorkflowCatalog catalog, IModelGuidanceCatalog guidance, IHttpClientFactory httpClientFactory)
    {
        _settingsStore = settingsStore;
        _catalog = catalog;
        _guidance = guidance;
        _httpClientFactory = httpClientFactory;
    }

    public IImageEditOrchestrator CreateOrchestrator()
    {
        var settings = _settingsStore.Current;

        var llmHttp = _httpClientFactory.CreateClient();
        llmHttp.BaseAddress = new Uri(settings.LlmBaseUrl.TrimEnd('/') + "/");
        var llmClient = new OpenAiCompatibleLlmClient(llmHttp, Options.Create(new LlmOptions
        {
            BaseUrl = settings.LlmBaseUrl,
            ApiKey = settings.LlmApiKey,
            Model = settings.LlmModel,
            SupportsJsonSchema = settings.LlmSupportsJsonSchema,
        }));

        var comfyOptions = Options.Create(new ComfyUiOptions
        {
            BaseUrl = settings.ComfyUiBaseUrl,
            Backend = settings.ComfyUiBackend,
            ApiKey = settings.ComfyCloudApiKey,
        });

        IComfyUiClient comfyClient;
        if (settings.ComfyUiBackend == ComfyUiBackend.Cloud)
        {
            var cloudHttp = _httpClientFactory.CreateClient(ComfyCloudHttpClientName);
            cloudHttp.BaseAddress = new Uri(settings.ComfyUiBaseUrl.TrimEnd('/') + "/");
            comfyClient = new ComfyCloudClient(cloudHttp, comfyOptions);
        }
        else
        {
            var comfyHttp = _httpClientFactory.CreateClient();
            comfyHttp.BaseAddress = new Uri(settings.ComfyUiBaseUrl.TrimEnd('/') + "/");
            comfyClient = new ComfyUiClient(comfyHttp, comfyOptions);
        }

        return new ImageEditOrchestrator(llmClient, _catalog, _guidance, comfyClient, Options.Create(new OrchestratorOptions
        {
            MaxIterations = settings.MaxIterations,
        }));
    }
}
