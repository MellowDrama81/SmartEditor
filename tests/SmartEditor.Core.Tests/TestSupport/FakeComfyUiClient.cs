using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Tests.TestSupport;

internal sealed class FakeComfyUiClient : IComfyUiClient
{
    private readonly Func<int, byte[]> _resultFactory;

    public int CallCount { get; private set; }
    public List<IReadOnlyDictionary<Guid, string>?> ReceivedUploadMaps { get; } = [];

    public FakeComfyUiClient(Func<int, byte[]>? resultFactory = null)
    {
        _resultFactory = resultFactory ?? (i => [(byte)i]);
    }

    public Task<ComfyRunResult> RunWorkflowAsync(
        WorkflowDefinition workflow,
        EditRequest request,
        string refinedPrompt,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        CallCount++;
        ReceivedUploadMaps.Add(alreadyUploaded);

        var uploaded = alreadyUploaded ?? request.Images.ToDictionary(i => i.Id, i => $"uploaded-{i.Id:N}.png");
        return Task.FromResult(new ComfyRunResult(_resultFactory(CallCount), uploaded));
    }
}
