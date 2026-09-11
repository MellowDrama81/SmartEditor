using SmartEditor.Core.Models;

namespace SmartEditor.Core.Abstractions;

/// <summary>Loads the bundled per-model strengths/weaknesses notes the planning LLM uses to pick
/// between workflows backed by different underlying models.</summary>
public interface IModelGuidanceCatalog
{
    IReadOnlyList<ModelGuidance> GetAll();
}
