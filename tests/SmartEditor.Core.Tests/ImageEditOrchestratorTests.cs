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
    public async Task Records_a_mismatch_iteration_and_skips_comfy_when_image_count_is_out_of_range()
    {
        var llm = new FakeLlmClient(
            """{"workflowId":"wf-needs-2","refinedPrompt":"combine them","reasoning":"needs two"}""");
        var comfy = new FakeComfyUiClient();
        var catalog = new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow("wf-needs-2", minImages: 2, maxImages: 2));
        var orchestrator = new ImageEditOrchestrator(
            llm, catalog, new FakeModelGuidanceCatalog(), comfy, Options.Create(new OrchestratorOptions { MaxIterations = 1 }));

        // MakeRequest() supplies only 1 image, but the workflow requires 2.
        var session = await orchestrator.RunAsync(MakeRequest(), progress: null, CancellationToken.None);

        Assert.Equal(EditSessionStatus.ExhaustedAttempts, session.Status);
        Assert.Single(session.History);
        Assert.False(session.History[0].Satisfied);
        Assert.Null(session.History[0].ResultImageBytes);
        Assert.Contains("requires between 2 and 2", session.History[0].JudgeFeedback);
        Assert.Equal(0, comfy.CallCount);
        Assert.Null(session.FinalResultBytes);
    }
}
