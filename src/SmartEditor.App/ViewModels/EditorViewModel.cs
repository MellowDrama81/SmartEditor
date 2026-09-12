using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.App.Services;
using SmartEditor.Core.Abstractions;
using SmartEditor.Core.Configuration;
using SmartEditor.Core.Models;
using SmartEditor.Core.Services;

namespace SmartEditor.App.ViewModels;

public partial class EditorViewModel : TabViewModelBase
{
    private readonly IEditSessionFactory _sessionFactory;
    private readonly IFilePickerService _filePicker;
    private readonly IWorkflowCatalog _workflowCatalog;

    private CancellationTokenSource? _runCts;
    private byte[]? _finalResultBytes;

    /// <summary>Source-image id &#8594; Comfy filename, for images already known to be sitting on
    /// the backend (uploaded immediately after being picked locally, or added straight from the
    /// asset library) &mdash; seeded into the orchestrator so <c>Run</c> doesn't upload them again.</summary>
    private readonly Dictionary<Guid, string> _uploadedAssetNames = [];

    public ObservableCollection<SourceImageViewModel> Images { get; } = [];
    public ObservableCollection<IterationDisplayViewModel> History { get; } = [];

    /// <summary>The shared Assets-tab view model, reused here to back the "Add from Assets…"
    /// picker overlay rather than duplicating asset-browsing logic.</summary>
    public AssetsViewModel AssetLibrary { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Prompt { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsMaskEditorOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAssetPickerOpen { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFinalResult))]
    public partial Bitmap? FinalResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMask))]
    public partial byte[]? MaskBytes { get; set; }

    [ObservableProperty]
    public partial double BrushRadius { get; set; } = 32;

    [ObservableProperty]
    public partial bool IsErasing { get; set; }

    /// <summary>Per-tab override of the globally-configured max-iterations setting, seeded from
    /// Settings when this tab is created but independently adjustable afterward &mdash; changing
    /// it here never touches the saved global default, and later changing that default doesn't
    /// retroactively change tabs that already picked up their own starting value.</summary>
    [ObservableProperty]
    public partial int MaxIterations { get; set; }

    /// <summary>Workflows the user can pin this run to, filtered by the exact same rule
    /// (<see cref="WorkflowEligibility"/>) the LLM's own choices are restricted to &mdash; always
    /// starts with the "let the LLM decide" sentinel (a <c>null</c> <see cref="WorkflowOptionViewModel.Workflow"/>),
    /// which is also the default selection. Recomputed whenever the image count or mask presence
    /// changes, since those are exactly what the eligibility rule depends on.</summary>
    public ObservableCollection<WorkflowOptionViewModel> WorkflowOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedWorkflowDescription))]
    [NotifyPropertyChangedFor(nameof(PromptAccepted))]
    [NotifyPropertyChangedFor(nameof(CanToggleRefinePrompt))]
    [NotifyPropertyChangedFor(nameof(WillUseLlm))]
    [NotifyPropertyChangedFor(nameof(IterationsLabel))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial WorkflowOptionViewModel? SelectedWorkflowOption { get; set; }

    public bool HasSelectedWorkflowDescription => SelectedWorkflowOption?.Description is not null;

    /// <summary>False only when the user has pinned this run to a specific workflow that has no
    /// use for a text prompt at all (a handful of bundled workflows are purely mechanical image
    /// operations) &mdash; "let the LLM decide" always leaves this true, since which workflow ends
    /// up chosen isn't known yet. Drives whether the Prompt box is enabled/required.</summary>
    public bool PromptAccepted => SelectedWorkflowOption?.Workflow?.Capabilities.AcceptsPrompt ?? true;

    /// <summary>Whether the user is even asked to rewrite their prompt through the LLM before
    /// running &mdash; unchecking this only matters once a specific workflow is pinned (see
    /// <see cref="WillUseLlm"/>); while "let the LLM decide" is selected, the LLM is always needed
    /// just to choose a workflow, so this has no effect and stays disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WillUseLlm))]
    [NotifyPropertyChangedFor(nameof(IterationsLabel))]
    public partial bool RefinePromptWithLlm { get; set; } = true;

    /// <summary>The checkbox only means anything once a specific workflow is pinned and that
    /// workflow actually accepts a prompt &mdash; otherwise there's nothing for it to refine.</summary>
    public bool CanToggleRefinePrompt => SelectedWorkflowOption?.Workflow is not null && PromptAccepted;

    /// <summary>Whether <c>Run</c> will call the LLM at all. False only when a specific workflow is
    /// pinned AND its prompt won't be refined (either because the user unchecked
    /// <see cref="RefinePromptWithLlm"/>, or the workflow doesn't accept a prompt in the first
    /// place) &mdash; in that exact case there is nothing left for the LLM to decide, so Run skips
    /// it entirely and just submits the workflow with the prompt as typed. "Let the LLM decide"
    /// always needs the LLM to choose a workflow in the first place.</summary>
    public bool WillUseLlm => SelectedWorkflowOption?.Workflow is null || (PromptAccepted && RefinePromptWithLlm);

    /// <summary>"Max retries" while the LLM's plan-run-judge loop is driving things (it stops early
    /// once judged satisfactory); once that's skipped (see <see cref="WillUseLlm"/>) there's no
    /// judging at all, so the same number instead means "run it exactly this many times, with a
    /// fresh random seed each time, and show every result" — reflected here purely as a label
    /// change so the slider itself needs no separate "batch count" duplicate.</summary>
    public string IterationsLabel => WillUseLlm ? "Max retries:" : "Run this many times:";

    public bool HasMask => MaskBytes is not null;
    public bool CanEditMask => Images.Count > 0;
    public SourceImageViewModel? FirstImage => Images.Count > 0 ? Images[0] : null;
    public bool HasFinalResult => FinalResult is not null;

    public EditorViewModel(
        IEditSessionFactory sessionFactory, IFilePickerService filePicker, AssetsViewModel assetLibrary,
        AppSettingsStore settingsStore, IWorkflowCatalog workflowCatalog)
        : base("Untitled", isClosable: true)
    {
        _sessionFactory = sessionFactory;
        _filePicker = filePicker;
        _workflowCatalog = workflowCatalog;
        AssetLibrary = assetLibrary;
        MaxIterations = settingsStore.Current.MaxIterations;
        RefreshWorkflowOptions();
    }

    /// <summary>Rebuilds <see cref="WorkflowOptions"/> from the current image count/mask, keeping
    /// the current selection if it's still eligible (e.g. removing an unrelated image shouldn't
    /// reset a still-valid choice) and falling back to "let the LLM decide" if it's not (e.g. the
    /// selected workflow needed a mask that was just cleared).</summary>
    public void RefreshWorkflowOptions()
    {
        var previouslySelectedId = SelectedWorkflowOption?.Workflow?.Id;

        var eligible = WorkflowEligibility.Filter(_workflowCatalog.GetAll(), Images.Count, HasMask);
        WorkflowOptions.Clear();
        WorkflowOptions.Add(new WorkflowOptionViewModel(null));
        foreach (var workflow in eligible)
        {
            WorkflowOptions.Add(new WorkflowOptionViewModel(workflow));
        }

        SelectedWorkflowOption = previouslySelectedId is not null
            ? WorkflowOptions.FirstOrDefault(o => o.Workflow?.Id == previouslySelectedId) ?? WorkflowOptions[0]
            : WorkflowOptions[0];
    }

    [RelayCommand]
    private async Task AddImagesAsync()
    {
        if (Images.Count >= EditRequest.MaxImages)
        {
            StatusMessage = $"You can add at most {EditRequest.MaxImages} images.";
            return;
        }

        var picked = await _filePicker.PickImagesAsync();
        var added = new List<SourceImageViewModel>();
        foreach (var file in picked)
        {
            if (Images.Count >= EditRequest.MaxImages)
            {
                break;
            }

            var image = new SourceImageViewModel(new SourceImage(file.Name, file.Bytes));
            Images.Add(image);
            added.Add(image);
        }

        OnPropertyChanged(nameof(CanEditMask));
        OnPropertyChanged(nameof(FirstImage));
        RefreshWorkflowOptions();

        // Fire-and-forget, one per image: keeps "Add images…" itself fast, while still getting
        // each file into Comfy's asset library right away rather than only at Run time.
        foreach (var image in added)
        {
            _ = UploadToAssetLibraryAsync(image);
        }
    }

    private async Task UploadToAssetLibraryAsync(SourceImageViewModel image)
    {
        try
        {
            var comfy = _sessionFactory.CreateComfyClient();
            var name = await comfy.UploadInputAssetAsync(image.Model.OriginalBytes, image.Model.FileName, CancellationToken.None);
            _uploadedAssetNames[image.Model.Id] = name;
            AssetLibrary.AddAlreadyUploaded(name, image.Model.FileName, image.Model.OriginalBytes);
        }
        catch (Exception ex)
        {
            // Best-effort only: Run's own upload-on-demand path still uploads the image normally
            // if this didn't get there first, so a failure here doesn't block anything — just note it.
            StatusMessage = $"Could not add '{image.Model.FileName}' to the Comfy asset library yet: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RemoveImage(SourceImageViewModel image)
    {
        var wasFirst = Images.Count > 0 && ReferenceEquals(Images[0], image);
        Images.Remove(image);
        _uploadedAssetNames.Remove(image.Model.Id);
        if (wasFirst)
        {
            MaskBytes = null;
        }

        OnPropertyChanged(nameof(CanEditMask));
        OnPropertyChanged(nameof(FirstImage));
        RefreshWorkflowOptions();
    }

    [RelayCommand]
    private void OpenAssetPicker()
    {
        IsAssetPickerOpen = true;
        if (AssetLibrary.Assets.Count == 0 && !AssetLibrary.IsLoading)
        {
            AssetLibrary.RefreshCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void CloseAssetPicker() => IsAssetPickerOpen = false;

    [RelayCommand]
    private async Task AddAssetToSourcesAsync(AssetItemViewModel asset)
    {
        if (Images.Count >= EditRequest.MaxImages)
        {
            StatusMessage = $"You can add at most {EditRequest.MaxImages} images.";
            return;
        }

        byte[] bytes;
        try
        {
            bytes = asset.Bytes ?? await _sessionFactory.CreateComfyClient().DownloadInputAssetAsync(asset.Filename, CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not add '{asset.DisplayName}': {ex.Message}";
            return;
        }

        var sourceImage = new SourceImage(asset.DisplayName, bytes);
        Images.Add(new SourceImageViewModel(sourceImage));
        // Already sitting on Comfy under this exact filename — record it so Run doesn't upload
        // a redundant second copy.
        _uploadedAssetNames[sourceImage.Id] = asset.Filename;

        OnPropertyChanged(nameof(CanEditMask));
        OnPropertyChanged(nameof(FirstImage));
        RefreshWorkflowOptions();
        StatusMessage = $"Added '{asset.DisplayName}' from the asset library.";
    }

    [RelayCommand]
    private void OpenMaskEditor() => IsMaskEditorOpen = true;

    [RelayCommand]
    private void CloseMaskEditor() => IsMaskEditorOpen = false;

    public void ConfirmMask(byte[] pngBytes)
    {
        MaskBytes = pngBytes;
        IsMaskEditorOpen = false;
        RefreshWorkflowOptions();
    }

    [RelayCommand]
    private void ClearMask()
    {
        MaskBytes = null;
        RefreshWorkflowOptions();
    }

    private bool CanRun() => !IsRunning && (!PromptAccepted || !string.IsNullOrWhiteSpace(Prompt));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        History.Clear();
        FinalResult = null;
        _finalResultBytes = null;
        StatusMessage = WillUseLlm ? "Planning..." : "Generating...";
        _runCts = new CancellationTokenSource();

        try
        {
            var request = new EditRequest(
                Images.Select(i => i.Model).ToList(),
                Prompt,
                MaskBytes is { } mask ? new MaskImage(mask) : null);

            if (WillUseLlm)
            {
                await RunWithLlmAsync(request, _runCts.Token);
            }
            else
            {
                await RunBatchWithoutLlmAsync(request, _runCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Cancelled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not start: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    private async Task RunWithLlmAsync(EditRequest request, CancellationToken ct)
    {
        var orchestrator = _sessionFactory.CreateOrchestrator(Math.Clamp(MaxIterations, 1, OrchestratorOptions.HardMaxIterations));
        var progress = new Progress<EditIteration>(iteration =>
        {
            History.Add(new IterationDisplayViewModel(iteration));
            StatusMessage = iteration.Satisfied
                ? $"Iteration {iteration.Index}: satisfied."
                : $"Iteration {iteration.Index}: not satisfied — {iteration.JudgeFeedback}";

            // Every produced output goes to the asset library as it happens, not just the
            // run's final pick — an earlier, unsatisfied-but-still-useful iteration shouldn't
            // require re-running to get back. Fire-and-forget: the upload shouldn't hold up the
            // next iteration, and any failure is reported on the Assets tab's own status.
            if (iteration.ResultImageBytes is { } resultBytes)
            {
                _ = AssetLibrary.AddGeneratedResultAsync(resultBytes, $"result-{DateTime.Now:yyyyMMdd-HHmmss}-{iteration.Index}.png");
            }
        });

        var session = await orchestrator.RunAsync(request, progress, ct, _uploadedAssetNames, SelectedWorkflowOption?.Workflow);

        if (session.FinalResultBytes is { } bytes)
        {
            _finalResultBytes = bytes;
            using var stream = new MemoryStream(bytes);
            FinalResult = new Bitmap(stream);
        }

        StatusMessage = session.Status switch
        {
            EditSessionStatus.Succeeded => "Done — the result satisfied the prompt.",
            EditSessionStatus.ExhaustedAttempts => "Stopped after the maximum number of attempts; showing the last result.",
            EditSessionStatus.Failed => $"Failed: {session.FailureReason}",
            _ => StatusMessage,
        };
    }

    /// <summary>No planner, no judge, no retry-on-dissatisfaction: <see cref="MaxIterations"/> is
    /// just how many times to submit the same pinned workflow with the prompt exactly as typed,
    /// each with its own fresh random seed (already how <c>RunWorkflowAsync</c> seeds every call),
    /// keeping every result rather than only a final pick — there's no judge to prefer one over the
    /// others, so all of them go to History and the asset library, same as the LLM path already
    /// does for its own per-iteration results.</summary>
    private async Task RunBatchWithoutLlmAsync(EditRequest request, CancellationToken ct)
    {
        var workflow = SelectedWorkflowOption?.Workflow
                       ?? throw new InvalidOperationException("No workflow is selected.");
        var comfy = _sessionFactory.CreateComfyClient();
        IReadOnlyDictionary<Guid, string>? uploadedImages = _uploadedAssetNames;
        var runCount = Math.Clamp(MaxIterations, 1, OrchestratorOptions.HardMaxIterations);

        for (var i = 1; i <= runCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            StatusMessage = $"Generating {i}/{runCount}...";

            if (!_workflowCatalog.GetAll().Any(w => w.Id == workflow.Id)) throw new InvalidOperationException("Workflow is disabled or deleted.");
            var runResult = await comfy.RunWorkflowAsync(workflow, request, Prompt, uploadedImages, null, ct);
            uploadedImages = runResult.UploadedImageNames;

            var iteration = new EditIteration
            {
                Index = i,
                WorkflowId = workflow.Id,
                RefinedPrompt = Prompt,
                PlannerReasoning = "",
                ResultImageBytes = runResult.ResultBytes,
                Satisfied = true,
                JudgeFeedback = "Generated without LLM judging.",
            };
            History.Add(new IterationDisplayViewModel(iteration));
            _ = AssetLibrary.AddGeneratedResultAsync(runResult.ResultBytes, $"result-{DateTime.Now:yyyyMMdd-HHmmss}-{i}.png");

            _finalResultBytes = runResult.ResultBytes;
            using var stream = new MemoryStream(runResult.ResultBytes);
            FinalResult = new Bitmap(stream);
        }

        StatusMessage = runCount == 1 ? "Done — 1 result." : $"Done — {runCount} results (see history below).";
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand]
    private async Task SaveResultAsync()
    {
        if (_finalResultBytes is null)
        {
            return;
        }

        var saved = await _filePicker.SaveFileAsync(_finalResultBytes, "smarteditor-result.png");
        StatusMessage = saved ? "Result saved." : StatusMessage;
    }
}
