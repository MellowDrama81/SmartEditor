using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using SmartEditor.App.Services;
using SmartEditor.App.ViewModels;

namespace SmartEditor.App.Views;

public partial class MainView : UserControl
{
    private IInputPane? _inputPane;

    public MainView()
    {
        InitializeComponent();

        AttachedToVisualTree += (_, _) =>
        {
            if (App.Services.GetService<IFilePickerService>() is AvaloniaFilePickerService picker)
            {
                picker.SetHost(TopLevel.GetTopLevel(this));
            }

            // Avalonia doesn't yet shift focused content above the on-screen keyboard on its own
            // (tracked upstream: https://github.com/AvaloniaUI/Avalonia/issues/13319) — on
            // Android/iOS the keyboard just draws over whatever the user is typing into otherwise.
            // Null on desktop (no software keyboard concept there), so this is a no-op there.
            if (TopLevel.GetTopLevel(this)?.InputPane is { } inputPane)
            {
                _inputPane = inputPane;
                _inputPane.StateChanged += OnInputPaneStateChanged;
            }
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (_inputPane is not null)
            {
                _inputPane.StateChanged -= OnInputPaneStateChanged;
                _inputPane = null;
            }
        };
    }

    /// <summary>Pushes this whole view's content up above the keyboard by exactly its height, then
    /// back down once it closes. Applied here, on the single root view every screen (including
    /// every overlay dialog — settings, assets, mask editor, etc., which are all children of this
    /// same view) lives inside of, rather than needing every individual screen to handle it.</summary>
    private void OnInputPaneStateChanged(object? sender, InputPaneStateEventArgs e)
    {
        Margin = e.NewState == InputPaneState.Open
            ? new Thickness(0, 0, 0, e.EndRect.Height)
            : default;
    }

    /// <summary>The tab-header label is a read-only <see cref="TextBox"/> whenever its tab isn't
    /// active (see <see cref="TabViewModelBase.IsEditable"/>), which should still activate the tab
    /// on click like the rest of the header does. Registered for the tunnel (preview) phase, so it
    /// runs and can mark the event handled before the TextBox's own bubble-phase click handling
    /// (caret placement/focus) ever sees it — relying on plain bubble-phase routing here would race
    /// against that internal handling in an unspecified order. When the tab is already active this
    /// does nothing, so the click falls through normally to place the caret for editing.</summary>
    private void OnTabTitleLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.AddHandler(InputElement.PointerPressedEvent, OnTabTitlePointerPressed, RoutingStrategies.Tunnel);
        }
    }

    private void OnTabTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBox { DataContext: TabViewModelBase tab } || DataContext is not ShellViewModel shell)
        {
            return;
        }

        if (!ReferenceEquals(shell.SelectedTab, tab))
        {
            shell.SelectedTab = tab;
            e.Handled = true;
        }
    }

    /// <summary>Switching tabs flips the previously-active tab's title <see cref="TextBox"/> to
    /// <c>IsReadOnly</c> (see <see cref="TabViewModelBase.IsEditable"/>), but that alone doesn't
    /// move keyboard focus away from it &mdash; if the user was mid-rename when they clicked
    /// another tab, the caret kept blinking in the now-read-only box, looking editable when typing
    /// no longer did anything. Move focus to the TabControl itself on every selection change so
    /// that never lingers.</summary>
    private void OnTabControlSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is InputElement tabControl)
        {
            tabControl.Focus();
        }
    }
}
