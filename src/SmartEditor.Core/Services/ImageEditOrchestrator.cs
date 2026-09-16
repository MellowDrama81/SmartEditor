using Microsoft.Extensions.Options;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Runs the bounded plan &#8594; execute &#8594; judge retry loop.</summary>
public sealed class ImageEditOrchestrator : IImageEditOrchestrator
{
    private readonly IWorkflowCatalog _catalog;
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
        _catalog = catalog;
        _planner = new ImageEditPlanner(llm, catalog, guidance);
        _judge = new ImageEditJudge(llm);
        _comfy = comfy;
        _maxIterations = options.Value.MaxIterations;
    }

    public async Task<EditSession> RunAsync(
        EditRequest request,
        IProgress<EditIteration>? progress,
        CancellationToken ct,
        IReadOnlyDictionary<Guid, string>? alreadyUploaded = null,
        WorkflowDefinition? forcedWorkflow = null,
        IProgress<EditRunProgress>? runProgress = null,
        IProgress<ComfyJobUpdate>? jobUpdates = null)
    {
        var session = new EditSession { Request = request };
        IReadOnlyDictionary<Guid, string>? uploadedImages = alreadyUploaded;
        EditIteration? previousIteration = null;

        try
        {
            for (var i = 1; i <= _maxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                runProgress?.Report(new EditRunProgress(EditRunStage.Planning, i));

                WorkflowDefinition workflow;
                string refinedPrompt;
                string reasoning;
                IReadOnlyList<int> imageOrder;
                try
                {
                    (workflow, refinedPrompt, reasoning, imageOrder) = await _planner.PlanAsync(request, previousIteration, forcedWorkflow, ct);
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

                // No image-count or mask mismatch check here: ImageEditPlanner.PlanAsync only ever
                // offers (and only ever resolves an id against) workflows whose min/maxImages
                // already fit request.Images.Count and whose RequiresMask already matches whether
                // a mask was supplied, so `workflow` is guaranteed compatible by construction.

                // The planner sees the images in the user's original order every iteration (so its
                // own references to "image 1"/"image 2" stay consistent across retries), and
                // separately decides how they map onto the chosen workflow's slots. Reorder just
                // for this run — the judge below still gets the original request/order, since it's
                // only comparing the result against the source images, not caring which slot each
                // one filled.
                var orderedImages = imageOrder.Select(index => request.Images[index]).ToList();
                var runRequest = new EditRequest(orderedImages, request.Prompt, request.Mask);

                if (!_catalog.GetAll().Any(w => w.Id == workflow.Id))
                    throw new InvalidOperationException("Selected workflow is disabled or deleted.");

                ComfyRunResult runResult;
                try
                {
                    runProgress?.Report(new EditRunProgress(EditRunStage.Generating, i, 0));
                    var comfyProgress = runProgress is null
                        ? null
                        : new Progress<double>(completion =>
                            runProgress.Report(new EditRunProgress(EditRunStage.Generating, i, completion)));
                    var comfyJobState = runProgress is null
                        ? null
                        : new Progress<ComfyJobState>(state => runProgress.Report(new EditRunProgress(
                            state == ComfyJobState.Queued ? EditRunStage.Queued : EditRunStage.Generating, i)));
                    runResult = await _comfy.RunWorkflowAsync(
                        workflow, runRequest, refinedPrompt, uploadedImages, comfyProgress, ct, comfyJobState, jobUpdates);
                }
                catch (ComfyWorkflowException ex)
                {
                    // A failure ComfyUI itself reports (a bad graph, an out-of-memory node, a
                    // timeout, etc.) doesn't have to end the whole session — same retry budget and
                    // feedback loop as a judged-unsatisfactory result, so the next planning pass
                    // sees exactly why this attempt failed and can pick a different workflow (or
                    // just adjust the prompt, if the workflow is user-forced and can't change)
                    // instead of giving up immediately.
                    var failed = new EditIteration
                    {
                        Index = i,
                        WorkflowId = workflow.Id,
                        RefinedPrompt = refinedPrompt,
                        PlannerReasoning = reasoning,
                        ResultImageBytes = null,
                        Satisfied = false,
                        JudgeFeedback = $"ComfyUI failed to generate a result with this workflow: {ex.Message}",
                    };
                    session.History.Add(failed);
                    progress?.Report(failed);
                    previousIteration = failed;
                    continue;
                }

                uploadedImages = runResult.UploadedImageNames;

                // Publish the image before asking the LLM to judge it. Judging can be relatively
                // slow, and the user should be able to inspect/download a completed render while
                // that evaluation is still underway. The second report below replaces this same
                // iteration in the UI with the eventual judgement.
                progress?.Report(new EditIteration
                {
                    Index = i,
                    WorkflowId = workflow.Id,
                    RefinedPrompt = refinedPrompt,
                    PlannerReasoning = reasoning,
                    ResultImageBytes = runResult.ResultBytes,
                    ResultOutputFilename = runResult.OutputFilename,
                    Satisfied = false,
                    JudgeFeedback = "Evaluating result...",
                });

                bool satisfied;
                string feedback;
                try
                {
                    runProgress?.Report(new EditRunProgress(EditRunStage.Judging, i));
                    (satisfied, feedback) = await _judge.JudgeAsync(request, refinedPrompt, workflow, runResult.ResultBytes, ct);
                }
                catch (LlmResponseParseException ex)
                {
                    // Same reasoning as the planner's retry-tolerance below/above: some
                    // providers/models are noticeably less reliable at strict JSON-schema adherence
                    // than others, and a malformed judge response says nothing about whether the
                    // image itself is any good. Previously this propagated to the outer catch and
                    // killed the whole session outright after a perfectly good ComfyUI run — losing
                    // the generated image and, worse, silently stopping retries the very first time
                    // the judge (not the planner) stumbled over JSON formatting. Treat it as "not yet
                    // confirmed satisfactory" instead, so the loop keeps going.
                    satisfied = false;
                    feedback = $"The judge's response could not be parsed: {ex.Message} " +
                               "(This iteration's own image was generated fine — only judging it failed.)";
                }

                var iteration = new EditIteration
                {
                    Index = i,
                    WorkflowId = workflow.Id,
                    RefinedPrompt = refinedPrompt,
                    PlannerReasoning = reasoning,
                    ResultImageBytes = runResult.ResultBytes,
                    ResultOutputFilename = runResult.OutputFilename,
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
            // Not just the last iteration's bytes: a Comfy failure or malformed plan can now be the
            // last entry in the budget (both retry with null ResultImageBytes), which would
            // otherwise blank out a perfectly good image an earlier iteration already produced.
            session.FinalResultBytes = session.History.LastOrDefault(h => h.ResultImageBytes is not null)?.ResultImageBytes;
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
            // A hard failure (e.g. the next planning/judging call, or a transient Comfy error) doesn't
            // mean every earlier iteration produced nothing usable — fall back to the last iteration
            // that actually generated an image (a malformed-plan retry iteration has none) rather than
            // discarding a perfectly viewable, just judged-unsatisfactory result.
            session.FinalResultBytes = session.History.LastOrDefault(h => h.ResultImageBytes is not null)?.ResultImageBytes;
            return session;
        }
    }
}
