using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Tests.TestSupport;

internal sealed class FakeModelGuidanceCatalog : IModelGuidanceCatalog
{
    private readonly IReadOnlyList<ModelGuidance> _entries;

    public FakeModelGuidanceCatalog(params ModelGuidance[] entries) => _entries = entries;

    public IReadOnlyList<ModelGuidance> GetAll() => _entries;
}
