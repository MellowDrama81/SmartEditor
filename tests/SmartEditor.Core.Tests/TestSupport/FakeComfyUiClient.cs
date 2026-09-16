using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Tests.TestSupport;

internal sealed class FakeComfyUiClient : IComfyUiClient
{
    public bool SupportsJobRecovery => false;
    private readonly Func<int, byte[]> _resultFactory;

    public int CallCount { get; private set; }
    public List<IReadOnlyDictionary<Guid, string>?> ReceivedUploadMaps { get; } = [];
    public List<EditRequest> ReceivedRequests { get; } = [];
    public List<string> ReceivedWorkflowIds { get; } = [];

    /// <summary>1-based call numbers (matching <see cref="CallCount"/> after incrementing) on which
    /// to throw a <see cref="ComfyWorkflowException"/> instead of returning a result — simulates
    /// ComfyUI itself reporting a run failure.</summary>
    public HashSet<int> FailOnCallNumbers { get; } = [];

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
        CancellationToken ct,
        IProgress<ComfyJobState>? jobState = null,
        IProgress<ComfyJobUpdate>? jobUpdates = null)
    {
        CallCount++;
        ReceivedUploadMaps.Add(alreadyUploaded);
        ReceivedRequests.Add(request);
        ReceivedWorkflowIds.Add(workflow.Id);

        if (FailOnCallNumbers.Contains(CallCount))
        {
            throw new ComfyWorkflowException($"Simulated ComfyUI failure on call {CallCount}.");
        }

        var uploaded = alreadyUploaded ?? request.Images.ToDictionary(i => i.Id, i => $"uploaded-{i.Id:N}.png");
        return Task.FromResult(new ComfyRunResult(_resultFactory(CallCount), uploaded, $"output-{CallCount}.png"));
    }

    public List<AssetInfo> Assets { get; } = [];

    /// <summary>Simulates real server-side paging: <see cref="PageSize"/> assets per call.</summary>
    public int PageSize { get; set; } = int.MaxValue;

    public Task<AssetPage> ListInputAssetsPageAsync(string? cursor, CancellationToken ct)
    {
        var start = cursor is null ? 0 : int.Parse(cursor);
        var page = Assets.Skip(start).Take(PageSize).ToList();
        var next = start + page.Count;
        var nextCursor = next < Assets.Count ? next.ToString() : null;
        return Task.FromResult(new AssetPage(page, nextCursor));
    }

    public Task<byte[]> DownloadInputAssetAsync(string filename, CancellationToken ct) =>
        Task.FromResult<byte[]>([1, 2, 3]);

    public Task<string> UploadInputAssetAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var name = $"uploaded-{fileName}";
        Assets.Add(new AssetInfo(name, fileName));
        return Task.FromResult(name);
    }

    public Task<ComfyRunResult> RecoverWorkflowAsync(string jobId, CancellationToken ct) =>
        throw new NotSupportedException();
}
