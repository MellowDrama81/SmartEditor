using Avalonia.Threading;
using SmartEditor.App.ViewModels;
using SmartEditor.Core.Abstractions;
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

    public void Track(ComfyJobUpdate update)
    {
        try
        {
            if (update.State is ComfyJobState.Queued or ComfyJobState.Generating) _store.Track(update.JobId);
            else if (update.State is ComfyJobState.Completed or ComfyJobState.Failed) _store.Remove(update.JobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The remote job must keep running even if device storage is temporarily unavailable.
            // Reporting on the Assets surface makes the loss of recovery protection visible without
            // allowing a progress callback to abort a Cloud job after it has been submitted.
            Dispatcher.UIThread.Post(() => _assets.StatusMessage =
                "Generation recovery could not be saved; keep the app open until this run finishes.");
        }
    }

    public async Task RecoverPendingAsync()
    {
        IReadOnlyList<string> pending;
        try { pending = _store.GetPending(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _assets.StatusMessage =
                "Could not read the saved generation-recovery journal. Check available storage and try again.");
            return;
        }
        if (pending.Count == 0) return;

        var operationId = _keepAlive.Start("Recovering image generation...");
        try
        {
            foreach (var jobId in pending)
            {
                try
                {
                    var result = await _sessions.CreateComfyClient().RecoverWorkflowAsync(jobId, CancellationToken.None);
                    var added = await Dispatcher.UIThread.InvokeAsync(() => _assets.AddGeneratedResultAsync(
                        result.ResultBytes, $"recovered-{jobId}.png", result.OutputFilename));
                    if (added)
                    {
                        TryRemove(jobId);
                    }
                }
                catch (NotSupportedException)
                {
                    // Self-hosted jobs have no durable Cloud job endpoint to reconcile.
                    TryRemove(jobId);
                    continue;
                }
                catch (ComfyWorkflowException ex) when (ex.IsTerminal)
                {
                    TryRemove(jobId);
                    await Dispatcher.UIThread.InvokeAsync(() => _assets.StatusMessage =
                        $"A recovered generation failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    // Keep the id for another attempt on the next launch; this also covers a job
                    // that is still queued/running when the app comes back.
                    await Dispatcher.UIThread.InvokeAsync(() => _assets.StatusMessage =
                        $"Could not recover a generation yet: {ex.Message}");
                }
            }
        }
        finally { _keepAlive.Stop(operationId); }
    }

    private void TryRemove(string jobId)
    {
        try
        {
            _store.Remove(jobId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Keep processing the other jobs. Leaving this id in place is safe: a later launch may
            // try it again, but the user is told why the journal could not be cleared.
            Dispatcher.UIThread.Post(() => _assets.StatusMessage =
                "Recovered generation state could not be cleared; it may be checked again next launch.");
        }
    }
}
