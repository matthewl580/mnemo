using Avalonia.Layout;
using Avalonia.Threading;
using Mnemo.Core.Models;
using System;

namespace Mnemo.UI.Components.BlockEditor;

public partial class EditableBlock
{
    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
            _cachedParentEditor?.UnregisterRealizedEditableBlock(_viewModel, this);

        UnsubscribeFromViewModel();

        _viewModel = DataContext as BlockViewModel;
        _cachedParentEditor = null;
        _focusManager?.ClearCache();

        if (_viewModel != null && VisualRoot != null)
            FindParentBlockEditor()?.RegisterRealizedEditableBlock(_viewModel, this);

        SubscribeToViewModel();

        Dispatcher.UIThread.Post(() => WireUpBlockComponent(), DispatcherPriority.Render);

        if (_viewModel != null && _stateManager != null)
        {
            _stateManager.SetUpdatingFromViewModel();
            _stateManager.PreviousText = _viewModel.Content ?? string.Empty;
            Dispatcher.UIThread.Post(() => _stateManager.SetNormal(), DispatcherPriority.Loaded);
        }
    }

    private void SubscribeToViewModel()
    {
        if (_viewModel == null) return;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ContentChanged += OnViewModelContentChanged;
        UpdateEditableBlockAlignment();
    }

    private void UnsubscribeFromViewModel()
    {
        if (_viewModel == null) return;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.ContentChanged -= OnViewModelContentChanged;
    }

    private void OnViewModelContentChanged(BlockViewModel sender)
    {
        if (_viewModel?.Type is BlockType.Image or BlockType.Sketch)
            UpdateEditableBlockAlignment();
    }

    private void UpdateEditableBlockAlignment()
    {
        var stretchRow = _viewModel?.Type is BlockType.Divider or BlockType.Page or BlockType.Code;
        if (BlockContentChrome != null)
            BlockContentChrome.HorizontalAlignment = stretchRow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        if (BlockContentControl != null)
        {
            BlockContentControl.HorizontalAlignment = stretchRow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            BlockContentControl.HorizontalContentAlignment = stretchRow ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        }

        if (_viewModel?.Type == BlockType.Image)
            this.HorizontalAlignment = ParseImageAlign(_viewModel);
        else if (_viewModel?.Type == BlockType.Sketch)
            this.HorizontalAlignment = ParseSketchAlign(_viewModel);
        else
            this.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private static HorizontalAlignment ParseImageAlign(BlockViewModel vm) =>
        vm.ImageAlign.Trim().ToLowerInvariant() switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };

    private static HorizontalAlignment ParseSketchAlign(BlockViewModel vm) =>
        vm.SketchAlign.Trim().ToLowerInvariant() switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left,
        };

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_stateManager == null || _viewModel == null) return;

        switch (e.PropertyName)
        {
            case nameof(BlockViewModel.Type):
                UpdateEditableBlockAlignment();
                Dispatcher.UIThread.Post(() => WireUpBlockComponent(), DispatcherPriority.Loaded);
                break;

            case nameof(BlockViewModel.IsFocused):
                if (_viewModel.IsFocused)
                {
                    _focusManager?.ClearCache();

                    var parentEditor = FindParentBlockEditor();
                    if (parentEditor?.IsCrossBlockSelectingActive == true) break;

                    var alreadyFocused = _focusManager?.GetFocusedTextBox() != null;

                    if (_viewModel.PendingCaretPixelX.HasValue)
                    {
                        var px = _viewModel.PendingCaretPixelX.Value;
                        var lastLine = _viewModel.PendingCaretPlaceOnLastLine;
                        _viewModel.PendingCaretPixelX = null;
                        _viewModel.PendingCaretPlaceOnLastLine = false;
                        _focusManager?.FocusTextBoxAtHorizontalOffset(px, lastLine);
                    }
                    else if (_viewModel.PendingCaretIndex.HasValue)
                    {
                        var caretIndex = _viewModel.PendingCaretIndex.Value;
                        _viewModel.PendingCaretIndex = null;
                        _focusManager?.FocusTextBox(caretIndex);
                    }
                    else if (!alreadyFocused)
                    {
                        _focusManager?.FocusTextBox();
                    }
                }
                else
                {
                    _slashCoord?.Close();
                    if (_toolbar?.IsOpen == true)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_viewModel != null && !_viewModel.IsFocused)
                                _toolbar?.Close();
                        }, DispatcherPriority.Input);
                    }
                }
                break;

            case nameof(BlockViewModel.Content):
                if (_stateManager != null)
                {
                    var currentText = _viewModel.Content ?? string.Empty;
                    var currentEditor = _focusManager?.GetCurrentTextBox();
                    var editorText = currentEditor?.Text ?? string.Empty;

                    if (editorText == currentText)
                    {
                        _stateManager.PreviousText = currentText;
                        return;
                    }

                    _stateManager.SetUpdatingFromViewModel();
                    _stateManager.PreviousText = currentText;
                    _focusManager?.ClearCache();
                    Dispatcher.UIThread.Post(() => _stateManager.SetNormal(), DispatcherPriority.Render);
                }
                break;
        }
    }
}
