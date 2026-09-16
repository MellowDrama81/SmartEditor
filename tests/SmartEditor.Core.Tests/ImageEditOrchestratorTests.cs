using Microsoft.Extensions.Options;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;
using SmartEditor.Core.Services;
using SmartEditor.Core.Tests.TestSupport;
using Xunit;

namespace SmartEditor.Core.Tests;

public class ImageEditOrchestratorTests
{
    private sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static EditRequest MakeRequest() =>
        new([new SourceImage("in.png", TestImages.TinyPng)], "make it blue");

    private static ImageEditOrchestrator MakeOrchestrator(FakeLlmClient llm, FakeComfyUiClient comfy, int maxIterations = 3) =>
        new(llm, new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow()), new FakeModelGuidanceCatalog(), comfy,
            Options.Create(new OrchestratorOptions { MaxIterations = maxIterations }));

    [Fact]
    public async Task Stops_on_first_satisfied_result()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Single(session.History);
        Assert.True(session.History[0].Satisfied);
        Assert.Equal(1, comfy.CallCount);
        Assert.NotNull(session.FinalResultBytes);
    }

    [Fact]
    public async Task Reports_when_planning_moves_into_generation_and_judging()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var events = new List<EditRunProgress>();

        await MakeOrchestrator(llm, new FakeComfyUiClient()).RunAsync(
            MakeRequest(), progress: null, CancellationToken.None,
            runProgress: new ImmediateProgress<EditRunProgress>(events.Add));

        Assert.Collection(events,
            update => Assert.Equal(new EditRunProgress(EditRunStage.Planning, 1), update),
            update => Assert.Equal(new EditRunProgress(EditRunStage.Generating, 1, 0), update),
            update => Assert.Equal(new EditRunProgress(EditRunStage.Judging, 1), update));
    }

    [Fact]
    public async Task Publishes_the_image_before_its_llm_evaluation_finishes()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var updates = new List<EditIteration>();

        await MakeOrchestrator(llm, new FakeComfyUiClient()).RunAsync(
            MakeRequest(), new ImmediateProgress<EditIteration>(updates.Add), CancellationToken.None);

        Assert.Collection(updates,
            pending =>
            {
                Assert.NotNull(pending.ResultImageBytes);
                Assert.Equal("Evaluating result...", pending.JudgeFeedback);
            },
            evaluated =>
            {
                Assert.NotNull(evaluated.ResultImageBytes);
                Assert.True(evaluated.Satisfied);
                Assert.Equal("looks great", evaluated.JudgeFeedback);
            });
    }

    [Fact]
    public async Task Stops_at_max_iterations_when_never_satisfied()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"attempt 1","reasoning":"r1"}""",
            """{"satisfied":false,"feedback":"needs more blue"}""",
            """{"workflowId":"wf1","refinedPrompt":"attempt 2","reasoning":"r2"}""",
            """{"satisfied":false,"feedback":"still not blue enough"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy, maxIterations: 2);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.ExhaustedAttempts, session.Status);
        Assert.Equal(2, session.History.Count);
        Assert.All(session.History, i => Assert.False(i.Satisfied));
        Assert.Equal(2, comfy.CallCount);
        Assert.Equal(session.History[^1].ResultImageBytes, session.FinalResultBytes);
    }

    [Fact]
    public async Task Exhausts_its_retry_budget_instead_of_looping_forever_when_comfy_keeps_failing()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"attempt 1","reasoning":"r1"}""",
            """{"workflowId":"wf1","refinedPrompt":"attempt 2","reasoning":"r2"}""");
        var comfy = new FakeComfyUiClient();
        comfy.FailOnCallNumbers.Add(1);
        comfy.FailOnCallNumbers.Add(2);
        var orchestrator = MakeOrchestrator(llm, comfy, maxIterations: 2);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.ExhaustedAttempts, session.Status);
        Assert.Equal(2, session.History.Count);
        Assert.All(session.History, i => Assert.False(i.Satisfied));
        Assert.All(session.History, i => Assert.Null(i.ResultImageBytes));
        Assert.Null(session.FinalResultBytes); // nothing was ever actually generated
        Assert.Equal(2, comfy.CallCount);
    }

    [Fact]
    public async Task Reuses_cached_upload_names_after_the_first_iteration()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"attempt 1","reasoning":"r1"}""",
            """{"satisfied":false,"feedback":"try again"}""",
            """{"workflowId":"wf1","refinedPrompt":"attempt 2","reasoning":"r2"}""",
            """{"satisfied":true,"feedback":"good"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy, maxIterations: 3);

        await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(2, comfy.ReceivedUploadMaps.Count);
        Assert.Null(comfy.ReceivedUploadMaps[0]);
        Assert.NotNull(comfy.ReceivedUploadMaps[1]);
    }

    [Fact]
    public async Task Seeds_the_first_iteration_with_a_caller_supplied_upload_map()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy);
        var request = MakeRequest();
        var seed = new Dictionary<Guid, string> { [request.Images[0].Id] = "already-on-comfy.png" };

        await orchestrator.RunAsync(request, progress: null, CancellationToken.None, alreadyUploaded: seed);

        // Unlike the null-seeded case, the very first call already carries the pre-known mapping —
        // proving an image uploaded (or picked from the asset library) before Run was clicked isn't
        // uploaded a second time.
        Assert.Single(comfy.ReceivedUploadMaps);
        Assert.Same(seed, comfy.ReceivedUploadMaps[0]);
    }

    [Fact]
    public async Task Includes_model_guidance_matching_the_catalog_in_the_planner_system_prompt()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow());
        var guidance = new FakeModelGuidanceCatalog(new ModelGuidance
        {
            Id = "test-model",
            DisplayName = "Test Model",
            WorkflowIds = ["wf1"],
            BestFor = ["editing test fixtures"],
            StrugglesWith = ["anything not scripted"],
        });
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, guidance, comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        var systemPrompt = llm.Requests[0].SystemPrompt; // the planner's call, before the judge's
        Assert.Contains("Test Model", systemPrompt);
        Assert.Contains("editing test fixtures", systemPrompt);
        Assert.Contains("anything not scripted", systemPrompt);
    }

    [Fact]
    public async Task Tolerates_stray_punctuation_an_llm_echoes_around_a_workflow_id()
    {
        // Observed live with MiniMax M3: the catalog is rendered as "- id: <id>" and the model
        // occasionally echoes the leading ": " back, e.g. ":wf1" instead of "wf1".
        var llm = new FakeLlmClient(
            """{"workflowId":":wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Single(session.History);
        Assert.Equal("wf1", session.History[0].WorkflowId);
    }

    [Fact]
    public async Task Retries_instead_of_failing_the_session_when_a_plan_is_unrecoverably_malformed()
    {
        var llm = new FakeLlmClient(
            // Genuinely unrecognizable id and an empty prompt — not just cosmetic noise.
            """{"workflowId":"","refinedPrompt":"","reasoning":""}""",
            """{"workflowId":"wf1","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy, maxIterations: 2);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal(2, session.History.Count);
        Assert.False(session.History[0].Satisfied);
        Assert.Null(session.History[0].ResultImageBytes);
        Assert.True(session.History[1].Satisfied);
        Assert.Equal(1, comfy.CallCount); // the malformed attempt never reached Comfy at all
    }

    [Fact]
    public async Task Retries_with_a_different_workflow_instead_of_failing_the_session_when_comfy_fails()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"attempt 1","reasoning":"r1"}""",
            """{"workflowId":"wf2","refinedPrompt":"attempt 2","reasoning":"r2 - avoiding wf1"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        comfy.FailOnCallNumbers.Add(1); // wf1's run fails; wf2 should succeed
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow("wf1"), FakeWorkflowCatalog.SimpleWorkflow("wf2"));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 2 }));

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal(2, session.History.Count);
        // The failed attempt is recorded like any other retry (with null bytes, since nothing was
        // generated) and its feedback names what actually went wrong, so the planner — and anyone
        // looking at History afterward — can see why it moved on to a different workflow.
        Assert.False(session.History[0].Satisfied);
        Assert.Null(session.History[0].ResultImageBytes);
        Assert.Contains("Simulated ComfyUI failure", session.History[0].JudgeFeedback);
        Assert.True(session.History[1].Satisfied);
        Assert.Equal(["wf1", "wf2"], comfy.ReceivedWorkflowIds);
        Assert.NotNull(session.FinalResultBytes);
    }

    [Fact]
    public async Task Treats_an_unparsable_judge_response_as_unsatisfied_instead_of_failing_the_session()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"attempt 1","reasoning":"r1"}""",
            // Not JSON at all — simulates a provider that ignores the schema for the judge call
            // specifically (the planner call above is fine, so this isn't a planner-retry case).
            "the image looks pretty good to me",
            """{"workflowId":"wf1","refinedPrompt":"attempt 2","reasoning":"r2"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var orchestrator = MakeOrchestrator(llm, comfy, maxIterations: 2);

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal(2, session.History.Count);
        // Unlike a malformed plan (never reaches Comfy), a malformed judge response happens AFTER
        // a real ComfyUI run — that generated image must not be lost just because judging it failed.
        Assert.False(session.History[0].Satisfied);
        Assert.NotNull(session.History[0].ResultImageBytes);
        Assert.Contains("could not be parsed", session.History[0].JudgeFeedback);
        Assert.True(session.History[1].Satisfied);
        Assert.Equal(2, comfy.CallCount);
    }

    [Fact]
    public async Task Only_offers_the_llm_workflows_that_accept_the_supplied_image_count()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-0img","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(
            FakeWorkflowCatalog.SimpleWorkflow("wf-0img", minImages: 0, maxImages: 0),
            FakeWorkflowCatalog.SimpleWorkflow("wf-3img", minImages: 3, maxImages: 3));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        // No source images supplied — only "wf-0img" fits.
        var session = await orchestrator.RunAsync(new EditRequest([], "make a blue photo"), progress: null, CancellationToken.None);

        var systemPrompt = llm.Requests[0].SystemPrompt;
        Assert.Contains("wf-0img", systemPrompt);
        Assert.DoesNotContain("wf-3img", systemPrompt);
        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal("wf-0img", session.History[0].WorkflowId);
    }

    [Fact]
    public async Task Only_offers_mask_workflows_when_a_mask_is_provided()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-mask","refinedPrompt":"remove the object","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(
            FakeWorkflowCatalog.SimpleWorkflow("wf-mask", requiresMask: true),
            FakeWorkflowCatalog.SimpleWorkflow("wf-plain", requiresMask: false));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var image = new SourceImage("in.png", TestImages.TinyPng);
        var request = new EditRequest([image], "remove the object", new MaskImage(TestImages.TinyPng));
        var session = await orchestrator.RunAsync(request, progress: null, CancellationToken.None);

        var systemPrompt = llm.Requests[0].SystemPrompt;
        Assert.Contains("wf-mask", systemPrompt);
        Assert.DoesNotContain("wf-plain", systemPrompt);
        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal("wf-mask", session.History[0].WorkflowId);
    }

    [Fact]
    public async Task Reorders_images_per_the_llms_declared_slot_mapping_before_running_the_workflow()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"combine them","reasoning":"fits the ask","imageOrder":[1,0]}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow(minImages: 2, maxImages: 2));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var first = new SourceImage("first.png", TestImages.TinyPng);
        var second = new SourceImage("second.png", TestImages.TinyPng);
        var request = new EditRequest([first, second], "combine them");

        var session = await orchestrator.RunAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        // imageOrder [1,0] means slot 0 = the second supplied image, slot 1 = the first — reversed
        // from how the user actually added them.
        Assert.Equal([second, first], comfy.ReceivedRequests[0].Images);
    }

    [Fact]
    public async Task Defaults_to_the_original_image_order_when_the_llm_omits_imageOrder()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf1","refinedPrompt":"combine them","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow(minImages: 2, maxImages: 2));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var first = new SourceImage("first.png", TestImages.TinyPng);
        var second = new SourceImage("second.png", TestImages.TinyPng);
        var request = new EditRequest([first, second], "combine them");

        var session = await orchestrator.RunAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal([first, second], comfy.ReceivedRequests[0].Images);
    }

    [Fact]
    public async Task Retries_instead_of_failing_the_session_when_imageOrder_is_not_a_valid_permutation()
    {
        var llm = new FakeLlmClient(
            // Not a permutation of [0, 1] — index 0 repeated, index 1 never used.
            """{"workflowId":"wf1","refinedPrompt":"combine them","reasoning":"r1","imageOrder":[0,0]}""",
            """{"workflowId":"wf1","refinedPrompt":"combine them","reasoning":"r2","imageOrder":[1,0]}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow(minImages: 2, maxImages: 2));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 2 }));

        var first = new SourceImage("first.png", TestImages.TinyPng);
        var second = new SourceImage("second.png", TestImages.TinyPng);
        var request = new EditRequest([first, second], "combine them");

        var session = await orchestrator.RunAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal(2, session.History.Count);
        Assert.False(session.History[0].Satisfied);
        Assert.Equal(1, comfy.CallCount); // the invalid imageOrder never reached Comfy
    }

    [Fact]
    public async Task Only_offers_non_mask_workflows_when_no_mask_is_provided()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-plain","refinedPrompt":"make it blue","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(
            FakeWorkflowCatalog.SimpleWorkflow("wf-mask", requiresMask: true),
            FakeWorkflowCatalog.SimpleWorkflow("wf-plain", requiresMask: false));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        var systemPrompt = llm.Requests[0].SystemPrompt;
        Assert.Contains("wf-plain", systemPrompt);
        Assert.DoesNotContain("wf-mask", systemPrompt);
        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal("wf-plain", session.History[0].WorkflowId);
    }

    [Fact]
    public async Task Retries_when_the_llm_selects_a_workflow_outside_the_offered_image_count_range()
    {
        var llm = new FakeLlmClient(
            // Ignores the filtered catalog and asks for the 3-image workflow anyway.
            """{"workflowId":"wf-3img","refinedPrompt":"combine them","reasoning":"got confused"}""",
            """{"workflowId":"wf-0img","refinedPrompt":"a blue photo","reasoning":"fits the ask"}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(
            FakeWorkflowCatalog.SimpleWorkflow("wf-0img", minImages: 0, maxImages: 0),
            FakeWorkflowCatalog.SimpleWorkflow("wf-3img", minImages: 3, maxImages: 3));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 2 }));

        var session = await orchestrator.RunAsync(new EditRequest([], "make a blue photo"), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal(2, session.History.Count);
        Assert.False(session.History[0].Satisfied);
        Assert.Null(session.History[0].ResultImageBytes);
        Assert.Equal(1, comfy.CallCount); // the invalid selection never reached Comfy
        Assert.Equal("wf-0img", session.History[1].WorkflowId);
    }

    [Fact]
    public async Task Fails_immediately_without_calling_the_llm_when_no_workflow_accepts_the_supplied_image_count()
    {
        var llm = new FakeLlmClient(); // no scripted responses — must never be called
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow("wf-3img", minImages: 3, maxImages: 3));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 3 }));

        // No source images supplied, but the only workflow needs 3.
        var session = await orchestrator.RunAsync(new EditRequest([], "make a blue photo"), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.Failed, session.Status);
        Assert.Contains("0 source image(s)", session.FailureReason);
        Assert.Empty(llm.Requests);
        Assert.Equal(0, comfy.CallCount);
    }

    [Fact]
    public async Task Uses_the_forced_workflow_without_asking_the_llm_to_choose_one()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-forced","refinedPrompt":"make it blue","reasoning":"only option","imageOrder":[0]}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(
            FakeWorkflowCatalog.SimpleWorkflow("wf-forced"),
            FakeWorkflowCatalog.SimpleWorkflow("wf-other"));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var forced = catalog.GetAll().Single(w => w.Id == "wf-forced");
        var session = await orchestrator.RunAsync(
            MakeRequest(), progress: null, CancellationToken.None, alreadyUploaded: null, forcedWorkflow: forced);

        // The other eligible workflow is never even shown to the LLM as an option.
        var systemPrompt = llm.Requests[0].SystemPrompt;
        Assert.Contains("wf-forced", systemPrompt);
        Assert.DoesNotContain("wf-other", systemPrompt);
        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Equal("wf-forced", session.History[0].WorkflowId);
    }

    [Fact]
    public async Task Allows_an_empty_refinedPrompt_for_a_workflow_that_does_not_accept_a_prompt()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-no-prompt","refinedPrompt":"","reasoning":"purely mechanical","imageOrder":[0]}""",
            """{"satisfied":true,"feedback":"looks great"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow("wf-no-prompt", acceptsPrompt: false));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        var forced = catalog.GetAll().Single();
        var session = await orchestrator.RunAsync(
            MakeRequest(), progress: null, CancellationToken.None, alreadyUploaded: null, forcedWorkflow: forced);

        // An empty refinedPrompt would be a retry-able parse failure for a prompt-using workflow —
        // it must not be here, since the workflow has nothing to do with it either way.
        Assert.Equal(EditSessionStatus.Succeeded, session.Status);
        Assert.Single(session.History);
        Assert.Equal(1, comfy.CallCount);
    }

    [Fact]
    public async Task Fails_immediately_without_calling_the_llm_when_the_forced_workflow_does_not_fit_the_request()
    {
        var llm = new FakeLlmClient(); // no scripted responses — must never be called
        var comfy = new FakeComfyUiClient();
        var forced = FakeWorkflowCatalog.SimpleWorkflow("wf-3img", minImages: 3, maxImages: 3);
        var catalog = new FakeWorkflowCatalog(forced);
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 3 }));

        // Only 1 source image supplied, but the forced workflow needs 3.
        var session = await orchestrator.RunAsync(
            MakeRequest(), progress: null, CancellationToken.None, alreadyUploaded: null, forcedWorkflow: forced);

        Assert.Equal(EditSessionStatus.Failed, session.Status);
        Assert.Contains("wf-3img", session.FailureReason);
        Assert.Empty(llm.Requests);
        Assert.Equal(0, comfy.CallCount);
    }

}
