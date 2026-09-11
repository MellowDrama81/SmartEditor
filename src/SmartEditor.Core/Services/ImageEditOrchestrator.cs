using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Runs the bounded plan &#8594; execute &#8594; judge retry loop.</summary>
public sealed class ImageEditOrchestrator : IImageEditOrchestrator
{
    private readonly ImageEditPlanner _planner;
    private readonly ImageEditJudge _judge;
    private readonly IComfyUiClient _comfy;
    private readonly int _maxIterations;

    public ImageEditOrchestrator(
        ILlmClient llm,
        IWorkflowCatalog catalog,
        IModelGuidanceCatalog guidance,
        IComfyUiClient comfy,
        IOptions<OrchestratorOptions> options)
    {
        _planner = new ImageEditPlanner(llm, catalog, guidance);
        _judge = new ImageEditJudge(llm);
        _comfy = comfy;
        _maxIterations = options.Value.MaxIterations;
    }

    public async Task<EditSession> RunAsync(EditRequest request, IProgress<EditIteration>? progress, CancellationToken ct)
    {
        var session = new EditSession { Request = request };
        IReadOnlyDictionary<Guid, string>? uploadedImages = null;
        EditIteration? previousIteration = null;

        try
        {
            for (var i = 1; i <= _maxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();

                var (workflow, refinedPrompt, reasoning) = await _planner.PlanAsync(request, previousIteration, ct);

                if (request.Images.Count < workflow.Capabilities.MinImages || request.Images.Count > workflow.Capabilities.MaxImages)
                {
                    var mismatch = new EditIteration
                    {
                        Index = i,
                        WorkflowId = workflow.Id,
                        RefinedPrompt = refinedPrompt,
                        PlannerReasoning = reasoning,
                        ResultImageBytes = null,
                        Satisfied = false,
                        JudgeFeedback = $"Workflow '{workflow.DisplayName}' requires between {workflow.Capabilities.MinImages} " +
                                        $"and {workflow.Capabilities.MaxImages} image(s), but {request.Images.Count} were supplied. " +
                                        "Choose a workflow that fits the supplied image count.",
                    };
                    session.History.Add(mismatch);
                    progress?.Report(mismatch);
                    previousIteration = mismatch;
                    continue;
                }

                var runResult = await _comfy.RunWorkflowAsync(workflow, request, refinedPrompt, uploadedImages, null, ct);
                uploadedImages = runResult.UploadedImageNames;

                var (satisfied, feedback) = await _judge.JudgeAsync(request, refinedPrompt, workflow, runResult.ResultBytes, ct);

                var iteration = new EditIteration
                {
                    Index = i,
                    WorkflowId = workflow.Id,
                    RefinedPrompt = refinedPrompt,
                    PlannerReasoning = reasoning,
                    ResultImageBytes = runResult.ResultBytes,
                    Satisfied = satisfied,
                    JudgeFeedback = feedback,
                };
                session.History.Add(iteration);
                progress?.Report(iteration);
                previousIteration = iteration;

                if (satisfied)
                {
                    session.Status = EditSessionStatus.Succeeded;
                    session.FinalResultBytes = runResult.ResultBytes;
                    return session;
                }
            }

            session.Status = EditSessionStatus.ExhaustedAttempts;
            session.FinalResultBytes = session.History[^1].ResultImageBytes;
            return session;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            session.Status = EditSessionStatus.Failed;
            session.FailureReason = ex.Message;
            return session;
        }
    }
}
