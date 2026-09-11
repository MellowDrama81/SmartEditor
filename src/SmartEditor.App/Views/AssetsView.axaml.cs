using Avalonia.Controls;
using SmartEditor.App.ViewModels;

namespace SmartEditor.App.Views;

public partial class AssetsView : UserControl
{
    public AssetsView()
    {
        InitializeComponent();
    }

    // Loads the next page once the viewer has scrolled near the bottom, rather than fetching the
    // whole (potentially huge) asset listing up front.
    private void OnAssetsScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || DataContext is not AssetsViewModel viewModel)
        {
            return;
        }

        var distanceFromBottom = scrollViewer.Extent.Height - (scrollViewer.Offset.Y + scrollViewer.Viewport.Height);
        if (distanceFromBottom < 400 && viewModel.LoadMoreCommand.CanExecute(null))
        {
            viewModel.LoadMoreCommand.Execute(null);
        }
    }
}
