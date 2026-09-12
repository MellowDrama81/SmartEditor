using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.Core.Services;

namespace SmartEditor.App.ViewModels;

public partial class WorkflowsViewModel : ViewModelBase
{
    private readonly ManagedWorkflowCatalog _catalog;
    private bool _creating;
    public ObservableCollection<ManagedWorkflow> Workflows { get; } = [];
    [ObservableProperty] public partial ManagedWorkflow? Selected { get; set; }
    [ObservableProperty] public partial string Id { get; set; } = "";
    [ObservableProperty] public partial string DisplayName { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool RequiresMask { get; set; }
    [ObservableProperty] public partial int MinImages { get; set; }
    [ObservableProperty] public partial int MaxImages { get; set; }
    [ObservableProperty] public partial string Graph { get; set; } = "";
    [ObservableProperty] public partial bool CanEdit { get; set; }
    [ObservableProperty] public partial bool CanEditId { get; set; }
    [ObservableProperty] public partial bool CanDelete { get; set; }
    [ObservableProperty] public partial bool ConfirmingDelete { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    public WorkflowsViewModel(ManagedWorkflowCatalog catalog)
    {
        _catalog = catalog;
        Reload();
    }
    private void Reload(string? id = null)
    {
        Workflows.Clear();
        foreach (var workflow in _catalog.GetManaged()) Workflows.Add(workflow);
        Selected = Workflows.FirstOrDefault(w => w.Definition.Id == id) ?? Workflows.FirstOrDefault();
    }
    partial void OnSelectedChanged(ManagedWorkflow? value)
    {
        ConfirmingDelete = false;
        Status = "";
        _creating = false;
        CanEditId = false;
        CanEdit = CanDelete = value is { IsBuiltIn: false };
        if (value is null) return;
        var w = value.Definition;
        Id = w.Id; DisplayName = w.DisplayName; Description = w.Description;
        RequiresMask = w.Capabilities.RequiresMask; MinImages = w.Capabilities.MinImages; MaxImages = w.Capabilities.MaxImages;
        try { Graph = File.ReadAllText(w.GraphFilePath); }
        catch (Exception ex) { Status = ex.Message; }
    }
    [RelayCommand] private void Add()
    {
        Selected = null;
        _creating = true; CanEdit = CanEditId = true; CanDelete = false;
        Id = ""; DisplayName = ""; Description = ""; RequiresMask = false; MinImages = MaxImages = 0; Graph = "{}";
        Status = "New custom workflow. Fill in the details and paste a ComfyUI API graph, then save.";
    }
    [RelayCommand] private void Save() => Attempt(() =>
    {
        if (!CanEdit) return;
        _catalog.SaveCustom(new(Id.Trim(), DisplayName.Trim(), Description.Trim(), RequiresMask, MinImages, MaxImages, Graph), _creating);
        Reload(Id.Trim()); Status = "Workflow saved.";
    });
    [RelayCommand] private void Copy() => Attempt(() =>
    {
        if (Selected is null) return;
        var source = Selected.Definition;
        var graph = File.ReadAllText(source.GraphFilePath);
        var ids = _catalog.GetManaged().Select(w => w.Definition.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseId = source.Id + "-copy";
        var newId = baseId;
        for (var suffix = 2; ids.Contains(newId); suffix++) newId = $"{baseId}-{suffix}";

        Selected = null;
        _creating = true;
        CanEdit = CanEditId = true;
        CanDelete = ConfirmingDelete = false;
        Id = newId;
        DisplayName = source.DisplayName + " (copy)";
        Description = source.Description;
        RequiresMask = source.Capabilities.RequiresMask;
        MinImages = source.Capabilities.MinImages;
        MaxImages = source.Capabilities.MaxImages;
        Graph = graph;
        Status = "Custom copy ready. Edit the details and save to create it. The new workflow will be enabled.";
    });
    [RelayCommand] private void ToggleEnabled() => Attempt(() =>
    {
        if (Selected is null) return;
        var id = Selected.Definition.Id;
        _catalog.SetEnabled(id, !Selected.IsEnabled); Reload(id);
    });
    [RelayCommand] private void RequestDelete() => ConfirmingDelete = CanDelete;
    [RelayCommand] private void CancelDelete() => ConfirmingDelete = false;
    [RelayCommand] private void Delete() => Attempt(() =>
    {
        if (!CanDelete || !ConfirmingDelete || Selected is null) return;
        _catalog.DeleteCustom(Selected.Definition.Id); Reload(); Status = "Custom workflow deleted.";
    });
    [RelayCommand] private void Cancel() { Reload(Selected?.Definition.Id); }
    private void Attempt(Action action)
    {
        try { action(); }
        catch (Exception ex) { Status = ex.Message; }
    }
}
