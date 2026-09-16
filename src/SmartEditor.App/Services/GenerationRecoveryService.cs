using Avalonia.Threading;
using SmartEditor.App.ViewModels;
using SmartEditor.Core.Models;

namespace SmartEditor.App.Services;

/// <summary>Reconciles Comfy Cloud jobs recorded before an Android process was killed.</summary>
public sealed class GenerationRecoveryService
{
    private readonly GenerationRecoveryStore _store;
    private readonly IEditSessionFactory _sessions;
    private readonly AssetsViewModel _assets;
    private readonly IGenerationKeepAlive _keepAlive;

    public GenerationRecoveryService(
        GenerationRecoveryStore store, IEditSessionFactory sessions, AssetsViewModel assets, IGenerationKeepAlive keepAlive)
    {
        _store = store;
        _sessions = sessions;
        _assets = assets;
        _keepAlive = keepAlive;
    }

    public Task TrackAsync(ComfyJobUpdate update) => update.State switch
    {
        ComfyJobState.Queued or ComfyJobState.Generating => _store.TrackAsync(update.JobId),
        ComfyJobState.Completed or ComfyJobState.Failed => _store.RemoveAsync(update.JobId),
        _ => Task.CompletedTask,
    };

    public async Task RecoverPendingAsync()
    {
        var pending = await _store.GetPendingAsync();
        if (pending.Count == 0) return;

        _keepAlive.Start("Recovering image generation...");
        try
        {
            foreach (var jobId in pending)
            {
                try
                {
                    var result = await _sessions.CreateComfyClient().RecoverWorkflowAsync(jobId, CancellationToken.None);
                    await Dispatcher.UIThread.InvokeAsync(() => _assets.AddGeneratedResultAsync(
                        result.ResultBytes, $"recovered-{jobId}.png", result.OutputFilename));
                    await _store.RemoveAsync(jobId);
                }
                catch (NotSupportedException)
                {
                    // Self-hosted jobs have no durable Cloud job endpoint to reconcile.
                    return;
                }
                catch
                {
                    // Keep the id for another attempt on the next launch; this also covers a job
                    // that is still queued/running when the app comes back.
                }
            }
        }
        finally { _keepAlive.Stop(); }
    }
}
