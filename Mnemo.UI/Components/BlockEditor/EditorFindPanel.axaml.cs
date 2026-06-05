using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Mnemo.UI.Components.BlockEditor;

public partial class EditorFindPanel : UserControl
{
    public EditorFindPanel()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnRootTunnelKeyDown, RoutingStrategies.Tunnel);
    }

    public BlockEditor? EditorHost { get; set; }

    private void OnRootTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || (e.KeyModifiers & KeyModifiers.Control) == 0)
            return;
        FindQueryTextBox.Focus();
        FindQueryTextBox.SelectAll();
        e.Handled = true;
    }

    private void FindQueryTextBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        EditorHost?.OnEditorFindPanelFindQueryTextChanged(sender, e);

    private void ReplaceQueryTextBox_OnTextChanged(object? sender, TextChangedEventArgs e) =>
        EditorHost?.OnEditorFindPanelReplaceQueryTextChanged(sender, e);

    private void FindOptionCheckBox_OnChanged(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelOptionChanged(sender, e);

    private void FindPreviousButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelFindPreviousClick(sender, e);

    private void FindNextButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelFindNextClick(sender, e);

    private void FindToggleReplaceButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelToggleReplaceClick(sender, e);

    private void FindCloseButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelCloseClick(sender, e);

    private void ReplaceCurrentButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelReplaceCurrentClick(sender, e);

    private void ReplaceAllButton_OnClick(object? sender, RoutedEventArgs e) =>
        EditorHost?.OnEditorFindPanelReplaceAllClick(sender, e);

    private void FindTextBox_OnKeyDown(object? sender, KeyEventArgs e) =>
        EditorHost?.OnEditorFindPanelFindTextKeyDown(sender, e);

    private void ReplaceTextBox_OnKeyDown(object? sender, KeyEventArgs e) =>
        EditorHost?.OnEditorFindPanelReplaceTextKeyDown(sender, e);
}
