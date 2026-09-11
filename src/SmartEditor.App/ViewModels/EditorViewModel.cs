using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.App.Services;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

public partial class EditorViewModel : TabViewModelBase
{
    private readonly IEditSessionFactory _sessionFactory;
    private readonly IFilePickerService _filePicker;

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

    public bool HasMask => MaskBytes is not null;
    public bool CanEditMask => Images.Count > 0;
    public SourceImageViewModel? FirstImage => Images.Count > 0 ? Images[0] : null;
    public bool HasFinalResult => FinalResult is not null;

    public EditorViewModel(IEditSessionFactory sessionFactory, IFilePickerService filePicker, AssetsViewModel assetLibrary)
        : base("Untitled", isClosable: true)
    {
        _sessionFactory = sessionFactory;
        _filePicker = filePicker;
        AssetLibrary = assetLibrary;
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
    }

    [RelayCommand]
    private void ClearMask() => MaskBytes = null;

    private bool CanRun() => !IsRunning && !string.IsNullOrWhiteSpace(Prompt);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        History.Clear();
        FinalResult = null;
        _finalResultBytes = null;
        StatusMessage = "Planning...";
        _runCts = new CancellationTokenSource();

        try
        {
            var request = new EditRequest(
                Images.Select(i => i.Model).ToList(),
                Prompt,
                MaskBytes is { } mask ? new MaskImage(mask) : null);

            var orchestrator = _sessionFactory.CreateOrchestrator();
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

            var session = await orchestrator.RunAsync(request, progress, _runCts.Token, _uploadedAssetNames);

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
