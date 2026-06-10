using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Models;
using Mnemo.UI;

namespace Mnemo.UI.Components.BlockEditor;

public partial class EditableBlock
{
    private void TextBox_GotFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _stateManager == null) return;

        _viewModel.IsFocused = true;

        if (!_keymapTextCaptureActive && Application.Current is App app && app.Services?.GetService<IKeyMap>() is { } keyMap)
        {
            keyMap.EnterTextCapture();
            _keymapTextCaptureActive = true;
        }

        if (sender is RichTextEditor editor)
            _stateManager.PreviousText = editor.Text;
        else if (sender is TextBox tb)
            _stateManager.PreviousText = tb.Text ?? string.Empty;
    }

    private void TextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel != null && _stateManager != null)
        {
            if (_toolbar?.IsOpen == true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var editor = _currentBlockComponent?.GetRichTextEditor() ?? _focusManager?.GetCurrentTextBox();
                    if (editor != null && editor.IsFocused) return;
                    if (_toolbar?.IsInteractingWithToolbar == true) return;
                    if (ShouldSuppressCompleteLostFocusForInEditorFocusRedirect()) return;
                    CompleteLostFocus();
                }, DispatcherPriority.Input);
                return;
            }
            if (!ShouldSuppressCompleteLostFocusForInEditorFocusRedirect())
                CompleteLostFocus();
        }
    }

    private bool ShouldSuppressCompleteLostFocusForInEditorFocusRedirect()
    {
        var blockEditor = FindParentBlockEditor();
        var top = TopLevel.GetTopLevel(this);
        var newFocus = top?.FocusManager?.GetFocusedElement() as Visual;
        if (newFocus == null || blockEditor == null) return false;
        if (!blockEditor.IsVisualAncestorOf(newFocus)) return false;

        var newFocusEditable = newFocus.FindAncestorOfType<EditableBlock>();
        if (newFocusEditable != null && !ReferenceEquals(newFocusEditable, this)) return false;

        return true;
    }

    private void CompleteLostFocus()
    {
        if (_viewModel == null || _stateManager == null) return;

        if (_keymapTextCaptureActive && Application.Current is App app && app.Services?.GetService<IKeyMap>() is { } keyMap)
        {
            keyMap.LeaveTextCapture();
            _keymapTextCaptureActive = false;
        }

        _viewModel.IsFocused = false;
        FindParentBlockEditor()?.FlushTypingBatch();
    }

    private void TextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (IsReadOnly) return;
        if (sender is not RichTextEditor editor || _viewModel == null || _stateManager == null) return;

        if (_stateManager.IsUpdatingFromViewModel) return;

        var text = editor.Text;
        var previousText = _stateManager.PreviousText;

        _slashCoord?.HandleTextChanged(text, editor);

        if (text == previousText) return;

        if (sender is RichTextEditor)
        {
            _stateManager.PreviousText = text;
            return;
        }

        _viewModel.PreviousContent = previousText;
        FindParentBlockEditor()?.TrackTypingEdit(_viewModel, previousText);
        _viewModel.Content = text;
        _stateManager.PreviousText = text;
    }

    private void TextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (IsReadOnly) return;
        if (_viewModel == null || sender is not RichTextEditor editor || _keyboardHandler == null) return;

        if (e.Key == Key.Escape && _formatHandler?.StickySubSup != null)
        {
            _formatHandler.ClearStickySubSup(editor);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && _backspaceHandledInTunnel)
        {
            _backspaceHandledInTunnel = false;
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Back)
            _backspaceHandledInTunnel = false;

        if ((e.KeyModifiers & KeyModifiers.Control) != 0
            && (e.KeyModifiers & KeyModifiers.Alt) == 0
            && (e.KeyModifiers & KeyModifiers.Shift) != 0
            && e.Key == Key.E
            && !IsReadOnly
            && _viewModel != null
            && _viewModel.Type != BlockType.Code)
        {
            _formatHandler?.ApplyInlineEquation();
            e.Handled = true;
            return;
        }

        if (IsNavigationKey(e.Key))
        {
            bool isRightArrow = e.Key == Key.Right;
            bool atSubSupBoundary = isRightArrow && (_formatHandler?.IsCaretAtTrailingSubSupBoundary(editor) ?? false);
            if (_formatHandler?.StickySubSup != null)
                _formatHandler.ClearStickySubSup(editor);
            if (atSubSupBoundary)
                editor.SetPendingSubSup(false, false);
        }

        if (_viewModel!.Type != BlockType.Image)
        {
            if (e.Key == Key.Space)
            {
                var typeForShortcut = _viewModel!.Type;
                Dispatcher.UIThread.Post(() =>
                {
                    if (_viewModel == null || _viewModel.Type != typeForShortcut) return;
                    var ed = _currentBlockComponent?.GetRichTextEditor();
                    if (ed == null) return;
                    _markdownDetector?.TryDetectShortcut(ed, _viewModel.Type);
                }, DispatcherPriority.Input);
            }

            var isSlashKey = e.Key == Key.Divide || e.Key == Key.OemQuestion || e.Key == Key.Oem2;
            if (isSlashKey && BlockEditorContentPolicy.IsVisuallyEmpty(editor.Text) && _stateManager != null && !(_slashCoord?.IsVisible ?? false))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    var currentText = editor.Text;
                    var stripped = BlockEditorContentPolicy.WithoutLegacySentinel(currentText);
                    if (stripped == "/" && !(_slashCoord?.IsVisible ?? false))
                        _slashCoord?.HandleTextChanged(stripped, editor);
                }, DispatcherPriority.Input);
            }

            if (e.Key == Key.Enter && (_slashCoord?.IsVisible ?? false))
            {
                if (_slashCoord!.TryConfirm()) { e.Handled = true; return; }
            }

            if (_slashCoord?.IsVisible ?? false)
            {
                if (_slashCoord!.TryNavigate(e.Key)) { e.Handled = true; return; }
            }
        }

        _keyboardHandler.HandleKeyDown(e, editor, _viewModel);
    }

    private static bool IsNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End;
}
