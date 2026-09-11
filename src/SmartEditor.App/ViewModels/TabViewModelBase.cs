using CommunityToolkit.Mvvm.ComponentModel;

namespace SmartEditor.App.ViewModels;

/// <summary>Shared shape for anything shown as a tab in <see cref="ShellViewModel"/>: a
/// user-editable label and whether the tab can be closed. <see cref="EditorViewModel"/> (one per
/// generation session, closable, many at once) and <see cref="AssetsViewModel"/> (a single
/// permanent tab) both derive from this so the tab strip can render either uniformly.</summary>
public abstract partial class TabViewModelBase : ViewModelBase
{
    [ObservableProperty]
    public partial string Title { get; set; }

    /// <summary>Kept in sync by <see cref="ShellViewModel"/> whenever <c>SelectedTab</c> changes
    /// &mdash; not driven by the TabControl's own selection binding directly, so the tab header
    /// template can tell "this item" apart from "the selected item" without a converter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool IsSelected { get; set; }

    public bool IsClosable { get; }

    /// <summary>Only the active, closable tab's title is editable: an inactive tab's header is a
    /// plain (read-only) label so clicking it just activates the tab, and the permanent Assets tab
    /// (not closable) is never renamable at all.</summary>
    public bool IsEditable => IsSelected && IsClosable;

    protected TabViewModelBase(string title, bool isClosable)
    {
        Title = title;
        IsClosable = isClosable;
    }
}
