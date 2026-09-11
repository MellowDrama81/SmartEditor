using Avalonia.Controls;
using Avalonia.Interactivity;
using SmartEditor.App.ViewModels;

namespace SmartEditor.App.Views;

public partial class EditorView : UserControl
{
    public EditorView()
    {
        InitializeComponent();
    }

    private void OnConfirmMaskClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not EditorViewModel editor)
        {
            return;
        }

        if (MaskCanvasControl.HasContent)
        {
            editor.ConfirmMask(MaskCanvasControl.ExportPng());
        }
        else
        {
            editor.CloseMaskEditorCommand.Execute(null);
        }
    }

    private void OnClearMaskCanvasClick(object? sender, RoutedEventArgs e)
    {
        MaskCanvasControl.Clear();
    }

    // Loads the next page of the asset picker's library once the viewer has scrolled near the
    // bottom, rather than fetching the whole (potentially huge) asset listing up front.
    private void OnAssetPickerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || DataContext is not EditorViewModel editor)
        {
            return;
        }

        var distanceFromBottom = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        if (distanceFromBottom < 400 && editor.AssetLibrary.LoadMoreCommand.CanExecute(null))
        {
            editor.AssetLibrary.LoadMoreCommand.Execute(null);
        }
    }
}
