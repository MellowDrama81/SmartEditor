using SmartEditor.Core.Models;
using SmartEditor.Core.Services;
using SmartEditor.Core.Tests.TestSupport;
using Xunit;

namespace SmartEditor.Core.Tests;

public sealed class ManagedWorkflowCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workflow-tests-" + Guid.NewGuid());
    private ManagedWorkflowCatalog Create() => new(new FakeWorkflowCatalog(FakeWorkflowCatalog.SimpleWorkflow()), _directory);
    private static CustomWorkflow Custom => new("custom", "Custom", "Generate an image", false, 0, 0,
        """{"1":{"class_type":"Test","inputs":{"text":"{{PROMPT:string}}","seed":{{SEED:seed}}}}}""");

    [Fact] public void Disabled_built_ins_are_hidden_and_preferences_survive_restart()
    {
        var catalog = Create();
        catalog.SetEnabled("wf1", false);
        Assert.Empty(catalog.GetAll());
        Assert.False(Assert.Single(Create().GetManaged()).IsEnabled);
        catalog.SetEnabled("wf1", true);
        Assert.Single(Create().GetAll());
    }
    [Fact] public void Custom_workflows_can_be_added_edited_disabled_and_deleted()
    {
        var catalog = Create();
        catalog.SaveCustom(Custom, true);
        var original = catalog.GetAll().Single(w => w.Id == "custom");
        Assert.True(original.Capabilities.AcceptsPrompt);
        catalog.SetEnabled("custom", false);
        catalog.SaveCustom(Custom with { DisplayName = "Edited", Graph = Custom.Graph.Replace("Test", "Other") }, false);
        var reloaded = Create();
        Assert.DoesNotContain(reloaded.GetAll(), w => w.Id == "custom");
        Assert.Equal("Edited", reloaded.GetManaged().Single(w => !w.IsBuiltIn).Definition.DisplayName);
        Assert.Contains("Test", File.ReadAllText(original.GraphFilePath));
        reloaded.DeleteCustom("custom");
        Assert.Single(Create().GetManaged());
    }
    [Fact] public void Built_ins_cannot_be_overwritten_or_deleted()
    {
        var catalog = Create();
        Assert.Throws<InvalidOperationException>(() => catalog.SaveCustom(Custom with { Id = "WF1" }, true));
        Assert.Throws<InvalidOperationException>(() => catalog.SaveCustom(Custom with { Id = "wf1" }, false));
        Assert.Throws<InvalidOperationException>(() => catalog.DeleteCustom("wf1"));
        Assert.Single(catalog.GetAll());
    }
    [Fact] public void Invalid_custom_changes_do_not_replace_saved_workflow()
    {
        var catalog = Create();
        catalog.SaveCustom(Custom, true);
        Assert.Throws<InvalidOperationException>(() => catalog.SaveCustom(Custom, true));
        Assert.Throws<InvalidOperationException>(() => catalog.SaveCustom(Custom with { Graph = "{}" }, false));
        Assert.Throws<InvalidOperationException>(() => catalog.SaveCustom(Custom with { MinImages = 2 }, false));
        Assert.Equal(Custom.Graph, File.ReadAllText(Create().GetAll().Single(w => w.Id == "custom").GraphFilePath));
    }
    [Fact] public async Task Disabled_forced_workflow_is_rejected_before_Llm_call()
    {
        var catalog = Create();
        var workflow = Assert.Single(catalog.GetAll());
        catalog.SetEnabled(workflow.Id, false);
        var planner = new ImageEditPlanner(new FakeLlmClient(), catalog, new FakeModelGuidanceCatalog());
        await Assert.ThrowsAsync<InvalidOperationException>(() => planner.PlanAsync(
            new EditRequest([new SourceImage("in.png", TestImages.TinyPng)], "edit"), null, workflow, CancellationToken.None));
    }
    [Fact] public void Corrupt_saved_state_does_not_prevent_built_ins_from_loading()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "workflows.json"), "{not json");

        var catalog = Create();

        Assert.Single(catalog.GetAll());
        Assert.NotEmpty(catalog.StartupWarning);
        Assert.True(File.Exists(Path.Combine(_directory, "workflows.json.bad")));
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
