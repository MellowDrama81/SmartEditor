using System.Text.Json;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Loads workflow definitions from a directory of paired <c>*.meta.json</c> (metadata)
/// and ComfyUI API-format graph <c>*.json</c> files carrying placeholder tokens. Validates every
/// meta file's graph eagerly, at construction, so a broken workflow is a startup error.</summary>
public sealed class FileWorkflowCatalog : IWorkflowCatalog
{
    private readonly List<WorkflowDefinition> _workflows;

    public FileWorkflowCatalog(string workflowsDirectory)
    {
        if (!Directory.Exists(workflowsDirectory))
        {
            throw new InvalidOperationException($"Workflows directory not found: {workflowsDirectory}");
        }

        _workflows = [];

        foreach (var metaPath in Directory.EnumerateFiles(workflowsDirectory, "*.meta.json").OrderBy(p => p))
        {
            _workflows.Add(LoadAndValidate(metaPath));
        }
    }

    public IReadOnlyList<WorkflowDefinition> GetAll() => _workflows;

    private static WorkflowDefinition LoadAndValidate(string metaPath)
    {
        WorkflowMetaDto meta;
        try
        {
            meta = JsonSerializer.Deserialize<WorkflowMetaDto>(File.ReadAllText(metaPath), JsonOptions)
                   ?? throw new InvalidOperationException("Empty metadata file.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Workflow metadata '{metaPath}' is not valid JSON: {ex.Message}", ex);
        }

        var directory = Path.GetDirectoryName(metaPath)!;
        var graphPath = Path.Combine(directory, meta.GraphFile);
        if (!File.Exists(graphPath))
        {
            throw new InvalidOperationException(
                $"Workflow '{meta.Id}' ({metaPath}) references graph file '{meta.GraphFile}', which does not exist.");
        }

        // Raw templates aren't valid JSON as-is: {{SEED:seed}} sits in a numeric (unquoted) position.
        // Every other token ({{PROMPT:string}}, {{UPLOADED_IMAGE_FILENAME:image}}, ...) already sits
        // inside a JSON string literal, so it doesn't need substitution to parse.
        var rawGraph = File.ReadAllText(graphPath);
        var acceptsPrompt = rawGraph.Contains("{{PROMPT:string}}", StringComparison.Ordinal);
        var parsableGraph = rawGraph.Replace("{{SEED:seed}}", "0", StringComparison.Ordinal);
        try
        {
            using var _ = JsonDocument.Parse(parsableGraph);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Workflow '{meta.Id}' graph '{graphPath}' is not valid JSON: {ex.Message}", ex);
        }

        return new WorkflowDefinition
        {
            Id = meta.Id,
            DisplayName = meta.DisplayName,
            Description = meta.Description,
            Capabilities = new WorkflowCapabilities(
                meta.Capabilities.RequiresMask, meta.Capabilities.MinImages, meta.Capabilities.MaxImages, acceptsPrompt),
            GraphFilePath = graphPath,
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class WorkflowMetaDto
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string Description { get; init; }
        public required string GraphFile { get; init; }
        public required CapabilitiesDto Capabilities { get; init; }
    }

    private sealed class CapabilitiesDto
    {
        public bool RequiresMask { get; init; }
        public int MinImages { get; init; }
        public int MaxImages { get; init; }
    }
}
