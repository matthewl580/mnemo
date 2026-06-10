using Avalonia.Threading;
using Mnemo.Core.Models;
using System.Collections.Generic;

namespace Mnemo.UI.Components.BlockEditor;

public partial class EditableBlock
{
    private void ConvertToBlockType(BlockType blockType) => SetBlockType(blockType);

    private void HandleConvertToTextPreservingContent()
    {
        if (_viewModel == null || _stateManager == null) return;

        var content = _currentBlockComponent?.GetRichTextEditor()?.Text ?? _viewModel.Content;

        _viewModel.NotifyStructuralChangeStarting();
        _stateManager.SetUpdatingFromViewModel();
        _viewModel.Type = BlockType.Text;

        Dispatcher.UIThread.Post(() =>
        {
            _stateManager.PreviousText = content;
            _viewModel.Content = content;
            _stateManager.SetNormal();
            _focusManager?.ClearCache();
            _focusManager?.FocusTextBox(0);
        }, DispatcherPriority.Render);
    }

    private void SetBlockType(BlockType blockType, Dictionary<string, object>? meta = null)
    {
        if (_viewModel == null || _stateManager == null) return;

        _viewModel.NotifyStructuralChangeStarting();

        using (_stateManager.BeginUpdate())
        {
            _viewModel.Type = blockType;
            if (meta != null
                && meta.TryGetValue(MarkdownShortcutDetector.ShortcutReplacementContentKey, out var repl)
                && repl is string replacementText)
            {
                _viewModel.Content = replacementText;
            }
            else
                _viewModel.Content = blockType == BlockType.Sketch ? "A -> B" : string.Empty;
            _stateManager.PreviousText = _viewModel.Content ?? string.Empty;
            _focusManager?.ClearCache();

            if (blockType == BlockType.NumberedList)
            {
                _viewModel.ListNumberIndex = meta != null
                    && meta.TryGetValue(MarkdownShortcutDetector.ShortcutListNumberIndexKey, out var listIndexObj)
                    && listIndexObj is int listIndex
                    ? listIndex
                    : 1;
            }

            if (meta != null)
            {
                if (blockType == BlockType.Code && meta.TryGetValue("language", out var langObj) && langObj != null)
                    _viewModel.CodeLanguage = langObj.ToString() ?? "csharp";
                foreach (var kvp in meta)
                {
                    if (kvp.Key == MarkdownShortcutDetector.ShortcutReplacementContentKey
                        || kvp.Key == MarkdownShortcutDetector.ShortcutListNumberIndexKey)
                        continue;
                    if (blockType == BlockType.Code && kvp.Key == "language")
                        continue;
                    _viewModel.Meta[kvp.Key] = kvp.Value;
                }
            }
        }

        var addedBlockBelow = blockType is BlockType.Divider or BlockType.Image or BlockType.Sketch
            && EnsureEditableBlockBelowIfNeeded();

        if (!addedBlockBelow)
        {
            _viewModel.IsFocused = true;
            Dispatcher.UIThread.Post(() => _focusManager?.FocusTextBox(), DispatcherPriority.Loaded);
        }
    }

    internal bool EnsureEditableBlockBelowIfNeeded()
    {
        if (_viewModel == null) return false;

        var editor = FindParentBlockEditor();
        if (editor == null) return false;

        var index = editor.Blocks.IndexOf(_viewModel);
        if (index < 0) return false;

        var nextIndex = index + 1;
        var hasEditableBelow = nextIndex < editor.Blocks.Count && IsEditableBlockType(editor.Blocks[nextIndex].Type);
        if (hasEditableBelow) return false;

        _viewModel.RequestNewBlockOfType(BlockType.Text);
        return true;
    }

    private static bool IsEditableBlockType(BlockType type) =>
        type is not BlockType.Divider and not BlockType.Image and not BlockType.Equation and not BlockType.Page and not BlockType.Sketch;
}
