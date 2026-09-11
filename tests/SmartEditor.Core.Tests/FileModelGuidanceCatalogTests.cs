using SmartEditor.Core.Services;
using Xunit;

namespace SmartEditor.Core.Tests;

public class FileModelGuidanceCatalogTests
{
    private static string GuidancePath => Path.Combine(AppContext.BaseDirectory, "Guidance", "model-guidance.json");
    private static string WorkflowsDir => Path.Combine(AppContext.BaseDirectory, "Workflows");

    [Fact]
    public void Loads_the_bundled_guidance_and_covers_every_bundled_workflow_exactly_once()
    {
        var guidance = new FileModelGuidanceCatalog(GuidancePath);
        var entries = guidance.GetAll();

        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.NotEmpty(entry.WorkflowIds);
            Assert.NotEmpty(entry.BestFor);
            Assert.NotEmpty(entry.StrugglesWith);
        }

        // Every id in the real bundled workflow catalog must be covered by exactly one guidance
        // entry, so the planner never silently omits guidance for a real workflow.
        var workflowIds = new FileWorkflowCatalog(WorkflowsDir).GetAll().Select(w => w.Id).ToHashSet();
        var guidedIds = entries.SelectMany(e => e.WorkflowIds).ToList();

        Assert.Equal(guidedIds.Count, guidedIds.Distinct().Count());
        Assert.Equal(workflowIds.OrderBy(id => id), guidedIds.OrderBy(id => id));
    }

    [Fact]
    public void Rejects_a_guidance_file_that_is_not_valid_json()
    {
        var tempDir = Directory.CreateTempSubdirectory("smarteditor-guidance-test").FullName;
        try
        {
            var path = Path.Combine(tempDir, "broken.json");
            File.WriteAllText(path, "{ this is not valid json ");

            var ex = Assert.Throws<InvalidOperationException>(() => new FileModelGuidanceCatalog(path));
            Assert.Contains("broken.json", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Rejects_a_workflow_id_listed_under_more_than_one_model()
    {
        var tempDir = Directory.CreateTempSubdirectory("smarteditor-guidance-test").FullName;
        try
        {
            var path = Path.Combine(tempDir, "guidance.json");
            File.WriteAllText(path, """
                [
                  {
                    "id": "model-a", "displayName": "Model A", "workflowIds": ["wf1"],
                    "bestFor": ["x"], "strugglesWith": ["y"]
                  },
                  {
                    "id": "model-b", "displayName": "Model B", "workflowIds": ["wf1"],
                    "bestFor": ["x"], "strugglesWith": ["y"]
                  }
                ]
                """);

            var ex = Assert.Throws<InvalidOperationException>(() => new FileModelGuidanceCatalog(path));
            Assert.Contains("wf1", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
