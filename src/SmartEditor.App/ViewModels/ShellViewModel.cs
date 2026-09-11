using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SmartEditor.App.ViewModels;

public partial class ShellViewModel : ViewModelBase
{
    private readonly Func<EditorViewModel> _editorFactory;
    private int _nextTabNumber = 1;

    /// <summary>Editor tabs — all closable. Assets are no longer a tab; they're shown via an
    /// overlay opened from the header button (see <see cref="IsAssetsOpen"/>) instead, the same way
    /// Settings is.</summary>
    public ObservableCollection<TabViewModelBase> Tabs { get; } = [];
    public SettingsViewModel Settings { get; }
    public AssetsViewModel Assets { get; }

    [ObservableProperty]
    public partial TabViewModelBase? SelectedTab { get; set; }

    partial void OnSelectedTabChanged(TabViewModelBase? oldValue, TabViewModelBase? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        if (newValue is not null)
        {
            newValue.IsSelected = true;
        }
    }

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAssetsOpen { get; set; }

    public ShellViewModel(Func<EditorViewModel> editorFactory, AssetsViewModel assets, SettingsViewModel settings)
    {
        _editorFactory = editorFactory;
        Settings = settings;
        Assets = assets;

        AddTab();
    }

    [RelayCommand]
    private void AddTab()
    {
        var tab = _editorFactory();
        tab.Title = $"Tab {_nextTabNumber++}";
        Tabs.Add(tab);
        SelectedTab = tab;
    }

    [RelayCommand]
    private void CloseTab(TabViewModelBase tab)
    {
        if (!tab.IsClosable)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.RemoveAt(index);

        if (Tabs.Count == 0)
        {
            // Always leave at least one editor tab open.
            AddTab();
            return;
        }

        if (ReferenceEquals(SelectedTab, tab))
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }
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

    [RelayCommand]
    private void OpenAssets()
    {
        IsAssetsOpen = true;
        if (Assets.Assets.Count == 0 && !Assets.IsLoading)
        {
            Assets.RefreshCommand.Execute(null);
        }
    }

    [RelayCommand]
    private void CloseAssets() => IsAssetsOpen = false;
}
