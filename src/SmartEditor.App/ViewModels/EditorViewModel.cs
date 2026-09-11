using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartEditor.App.Services;
using SmartEditor.Core.Models;

namespace SmartEditor.App.ViewModels;

public partial class EditorViewModel : ViewModelBase
{
    private readonly IEditSessionFactory _sessionFactory;
    private readonly IFilePickerService _filePicker;

    private CancellationTokenSource? _runCts;
    private byte[]? _finalResultBytes;

    public ObservableCollection<SourceImageViewModel> Images { get; } = [];
    public ObservableCollection<IterationDisplayViewModel> History { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial string Prompt { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial bool IsMaskEditorOpen { get; set; }

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

    public EditorViewModel(IEditSessionFactory sessionFactory, IFilePickerService filePicker)
    {
        _sessionFactory = sessionFactory;
        _filePicker = filePicker;
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
        foreach (var file in picked)
        {
            if (Images.Count >= EditRequest.MaxImages)
            {
                break;
            }

            Images.Add(new SourceImageViewModel(new SourceImage(file.Name, file.Bytes)));
        }

        OnPropertyChanged(nameof(CanEditMask));
        OnPropertyChanged(nameof(FirstImage));
    }

    [RelayCommand]
    private void RemoveImage(SourceImageViewModel image)
    {
        var wasFirst = Images.Count > 0 && ReferenceEquals(Images[0], image);
        Images.Remove(image);
        if (wasFirst)
        {
            MaskBytes = null;
        }

        OnPropertyChanged(nameof(CanEditMask));
        OnPropertyChanged(nameof(FirstImage));
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
            });

            var session = await orchestrator.RunAsync(request, progress, _runCts.Token);

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
