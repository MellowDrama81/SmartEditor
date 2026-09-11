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

                WorkflowDefinition workflow;
                string refinedPrompt;
                string reasoning;
                try
                {
                    (workflow, refinedPrompt, reasoning) = await _planner.PlanAsync(request, previousIteration, ct);
                }
                catch (LlmResponseParseException ex)
                {
                    // A malformed plan (unparsable JSON, an unrecognized workflowId, or an empty
                    // refinedPrompt) is a retry-able planning mistake, not a fatal session error —
                    // some providers/models are noticeably less reliable at strict JSON-schema
                    // adherence than others. Feed the failure back so the next attempt can correct
                    // it, the same way an image-count mismatch or an unsatisfied judge result does.
                    var malformed = new EditIteration
                    {
                        Index = i,
                        WorkflowId = "(unparsable plan)",
                        RefinedPrompt = "",
                        PlannerReasoning = "",
                        ResultImageBytes = null,
                        Satisfied = false,
                        JudgeFeedback = $"The previous response could not be used: {ex.Message} " +
                                        "Respond with ONLY the required JSON object: workflowId must be copied " +
                                        "exactly as listed in the catalog (no extra punctuation or whitespace), " +
                                        "and refinedPrompt must not be empty.",
                    };
                    session.History.Add(malformed);
                    progress?.Report(malformed);
                    previousIteration = malformed;
                    continue;
                }

                // No image-count mismatch check here: ImageEditPlanner.PlanAsync only ever offers
                // (and only ever resolves an id against) workflows whose min/maxImages already fit
                // request.Images.Count, so `workflow` is guaranteed compatible by construction.

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
