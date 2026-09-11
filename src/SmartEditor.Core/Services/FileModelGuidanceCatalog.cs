using System.Text.Json;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

/// <summary>Loads per-model strengths/weaknesses notes from a single bundled
/// <c>model-guidance.json</c> file (a JSON array of entries). Validated eagerly at construction so
/// a broken or drifted guidance file is a startup error, not a silent gap in what the planning LLM
/// sees.</summary>
public sealed class FileModelGuidanceCatalog : IModelGuidanceCatalog
{
    private readonly List<ModelGuidance> _entries;

    public FileModelGuidanceCatalog(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new InvalidOperationException($"Model guidance file not found: {filePath}");
        }

        List<GuidanceDto>? dtos;
        try
        {
            dtos = JsonSerializer.Deserialize<List<GuidanceDto>>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Model guidance file '{filePath}' is not valid JSON: {ex.Message}", ex);
        }

        if (dtos is null || dtos.Count == 0)
        {
            throw new InvalidOperationException($"Model guidance file '{filePath}' is empty.");
        }

        var seen = new HashSet<string>();
        _entries = [];
        foreach (var dto in dtos)
        {
            foreach (var workflowId in dto.WorkflowIds)
            {
                if (!seen.Add(workflowId))
                {
                    throw new InvalidOperationException(
                        $"Model guidance file '{filePath}': workflow id '{workflowId}' is listed under more than one model entry.");
                }
            }

            _entries.Add(new ModelGuidance
            {
                Id = dto.Id,
                DisplayName = dto.DisplayName,
                WorkflowIds = dto.WorkflowIds,
                BestFor = dto.BestFor,
                StrugglesWith = dto.StrugglesWith,
            });
        }
    }

    public IReadOnlyList<ModelGuidance> GetAll() => _entries;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GuidanceDto
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required List<string> WorkflowIds { get; init; }
        public required List<string> BestFor { get; init; }
        public required List<string> StrugglesWith { get; init; }
    }
}
