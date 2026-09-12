using System.Text.Json;
using System.Text.RegularExpressions;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Models;

namespace SmartEditor.Core.Services;

public sealed record CustomWorkflow(string Id, string DisplayName, string Description,
    bool RequiresMask, int MinImages, int MaxImages, string Graph);
public sealed record ManagedWorkflow(WorkflowDefinition Definition, bool IsBuiltIn, bool IsEnabled);

/// <summary>Persists user workflows and availability separately from the bundled files.</summary>
public sealed class ManagedWorkflowCatalog : IWorkflowCatalog
{
    private readonly IReadOnlyList<WorkflowDefinition> _builtIns;
    private readonly string _directory;
    private readonly object _gate = new();
    private State _state;
    private List<WorkflowDefinition> _custom;
    public event Action? Changed;

    public ManagedWorkflowCatalog(IWorkflowCatalog builtIns, string directory)
    {
        _builtIns = builtIns.GetAll();
        _directory = directory;
        Directory.CreateDirectory(directory);
        _state = File.Exists(StatePath)
            ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new() : new();
        _custom = _state.Custom.Select(Materialize).ToList();
    }

    private string StatePath => Path.Combine(_directory, "workflows.json");
    public IReadOnlyList<WorkflowDefinition> GetAll() => GetManaged().Where(w => w.IsEnabled).Select(w => w.Definition).ToList();
    public IReadOnlyList<ManagedWorkflow> GetManaged()
    {
        lock (_gate)
            return _builtIns.Select(w => new ManagedWorkflow(w, true, !_state.Disabled.Contains(w.Id)))
                .Concat(_custom.Select(w => new ManagedWorkflow(w, false, !_state.Disabled.Contains(w.Id))))
                .OrderBy(w => w.Definition.DisplayName).ToList();
    }

    public void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            if (!GetManaged().Any(w => w.Definition.Id == id)) throw new InvalidOperationException("Workflow no longer exists.");
            var disabled = _state.Disabled.Where(x => x != id).ToList();
            if (!enabled) disabled.Add(id);
            Commit(new State { Custom = _state.Custom, Disabled = disabled });
        }
        Changed?.Invoke();
    }

    public void SaveCustom(CustomWorkflow workflow, bool creating)
    {
        lock (_gate)
        {
            if (_builtIns.Any(w => string.Equals(w.Id, workflow.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Built-in workflows are read-only. Choose a unique ID.");
            var existing = _state.Custom.FirstOrDefault(w => string.Equals(w.Id, workflow.Id, StringComparison.OrdinalIgnoreCase));
            if (creating && existing is not null) throw new InvalidOperationException("A workflow with this ID already exists.");
            if (!creating && existing is null) throw new InvalidOperationException("Workflow no longer exists.");
            var definition = Materialize(workflow);
            Commit(new State { Custom = _state.Custom.Where(w => w != existing).Append(workflow).ToList(), Disabled = _state.Disabled });
            _custom = _custom.Where(w => w.Id != existing?.Id).Append(definition).ToList();
        }
        Changed?.Invoke();
    }

    public void DeleteCustom(string id)
    {
        lock (_gate)
        {
            if (_builtIns.Any(w => w.Id == id)) throw new InvalidOperationException("Built-in workflows cannot be deleted.");
            if (!_custom.Any(w => w.Id == id)) throw new InvalidOperationException("Workflow no longer exists.");
            Commit(new State { Custom = _state.Custom.Where(w => w.Id != id).ToList(), Disabled = _state.Disabled.Where(x => x != id).ToList() });
            _custom = _custom.Where(w => w.Id != id).ToList();
        }
        Changed?.Invoke();
    }

    private void Commit(State state)
    {
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, StatePath, true);
        _state = state;
    }

    private WorkflowDefinition Materialize(CustomWorkflow workflow)
    {
        if (string.IsNullOrWhiteSpace(workflow.Id) || !Regex.IsMatch(workflow.Id, "^[a-zA-Z0-9][a-zA-Z0-9_-]*$"))
            throw new InvalidOperationException("ID must contain only letters, numbers, hyphens or underscores.");
        if (string.IsNullOrWhiteSpace(workflow.DisplayName) || string.IsNullOrWhiteSpace(workflow.Description))
            throw new InvalidOperationException("Name and description are required.");
        if (workflow.MinImages < 0 || workflow.MaxImages < workflow.MinImages || workflow.MaxImages > 8 || (workflow.RequiresMask && workflow.MinImages == 0))
            throw new InvalidOperationException("Image counts must be between 0 and 8, with minimum ≤ maximum. Mask workflows require an image.");
        using var graph = JsonDocument.Parse(workflow.Graph.Replace("{{SEED:seed}}", "0", StringComparison.Ordinal));
        if (graph.RootElement.ValueKind != JsonValueKind.Object || !graph.RootElement.EnumerateObject().Any() ||
            graph.RootElement.EnumerateObject().Any(n => n.Value.ValueKind != JsonValueKind.Object ||
                !n.Value.TryGetProperty("class_type", out var type) || type.ValueKind != JsonValueKind.String ||
                !n.Value.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Object))
            throw new InvalidOperationException("Graph must be a ComfyUI API-format object of nodes with class_type and inputs.");
        // Immutable graph versions keep an edit from changing a graph an active run has selected.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(workflow.Graph)));
        var path = Path.Combine(_directory, hash + ".json");
        if (!File.Exists(path)) File.WriteAllText(path, workflow.Graph);
        return new WorkflowDefinition { Id = workflow.Id, DisplayName = workflow.DisplayName, Description = workflow.Description,
            Capabilities = new(workflow.RequiresMask, workflow.MinImages, workflow.MaxImages, workflow.Graph.Contains("{{PROMPT:string}}", StringComparison.Ordinal)), GraphFilePath = path };
    }

    public sealed class State
    {
        public List<CustomWorkflow> Custom { get; init; } = [];
        public List<string> Disabled { get; init; } = [];
    }
}
