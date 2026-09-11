using Microsoft.Extensions.Options;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;
using SmartEditor.Core.Services;
using SmartEditor.Core.Tests.TestSupport;
using Xunit;

namespace SmartEditor.Core.Tests;

public class ImageEditOrchestratorTests
{
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

}
