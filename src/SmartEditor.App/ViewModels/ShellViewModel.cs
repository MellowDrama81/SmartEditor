using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartEditor.App.ViewModels;

public partial class ShellViewModel : ViewModelBase
{
    public EditorViewModel Editor { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    public ShellViewModel(EditorViewModel editor, SettingsViewModel settings)
    {
        Editor = editor;
        Settings = settings;
    }

    [RelayCommand]
    private void OpenSettings() => IsSettingsOpen = true;

    [RelayCommand]
    private void SaveSettings()
    {
        Settings.Save();
        IsSettingsOpen = false;
    }

    [RelayCommand]
    private void CancelSettings() => IsSettingsOpen = false;
}
