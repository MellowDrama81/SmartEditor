using Avalonia.Controls;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SmartEditor.App.Services;
using SmartEditor.App.ViewModels;

namespace SmartEditor.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            if (App.Services.GetService<IFilePickerService>() is AvaloniaFilePickerService picker)
            {
                picker.SetHost(TopLevel.GetTopLevel(this));
            }
        };
    }

    private void OnConfirmMaskClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel shell && MaskCanvasControl.HasContent)
        {
            shell.Editor.ConfirmMask(MaskCanvasControl.ExportPng());
        }
        else if (DataContext is ShellViewModel s)
        {
            s.Editor.CloseMaskEditorCommand.Execute(null);
        }
    }

    private void OnClearMaskCanvasClick(object? sender, RoutedEventArgs e)
    {
        MaskCanvasControl.Clear();
    }
}
