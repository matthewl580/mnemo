using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Handles inline format commands (bold, italic, links, sub/sup, equations) for a single
/// <see cref="EditableBlock"/>. Owns <c>_stickySubSup</c> and the format-apply logic.
/// </summary>
internal sealed class InlineFormatCommandHandler
{
    private readonly EditableBlock _host;

    internal InlineFormatCommandHandler(EditableBlock host)
    {
        _host = host;
    }

    internal InlineFormatKind? StickySubSup { get; private set; }

    internal void TryApplyKeybind(InlineFormatKind kind, RichTextEditor editor)
    {
        if (_host.IsReadOnly || _host._viewModel == null || _host._viewModel.Type == BlockType.Code)
            return;

        if (kind == InlineFormatKind.Link)
        {
            _ = _host.HandleLinkShortcutAsync(editor);
            return;
        }

        if (kind is InlineFormatKind.Subscript or InlineFormatKind.Superscript)
        {
            HandleSubSupShortcut(editor, kind);
            return;
        }

        if (kind == InlineFormatKind.Bold
            && _host._viewModel.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 or BlockType.Heading4)
            return;

        string? color = null;
        if (kind == InlineFormatKind.Highlight
            && Avalonia.Application.Current?.TryFindResource("InlineHighlightColor", out var res) == true
            && res is Avalonia.Media.Color c)
            color = c.ToString();

        var blockEditor = _host.FindParentBlockEditor();
        if (blockEditor?.HasCrossBlockTextSelection() == true)
        {
            Apply(kind, color);
            return;
        }

        if (_host.GetSelectionRange() == null)
        {
            var word = editor.TryGetWordRangeAtCaret();
            if (word == null) return;
            editor.SelectionStart = word.Value.Start;
            editor.SelectionEnd = word.Value.End;
            editor.CaretIndex = word.Value.End;
        }

        Apply(kind, color);
    }

    /// <summary>Applies format respecting cross-block selection.</summary>
    internal void Apply(InlineFormatKind kind, string? color = null)
    {
        var blockEditor = _host.FindParentBlockEditor();
        if (blockEditor != null && blockEditor.HasCrossBlockTextSelection())
        {
            blockEditor.ApplyInlineFormatToCrossBlockSelection(kind, color);
            return;
        }

        ApplyInternal(kind, color);
    }

    /// <summary>Applies format to the current block's selection range only.</summary>
    internal void ApplyInternal(InlineFormatKind kind, string? color = null)
    {
        var editor = _host._currentBlockComponent?.GetRichTextEditor() ?? _host._focusManager?.GetCurrentTextBox();
        if (editor == null || _host._viewModel == null) return;

        if (kind == InlineFormatKind.Bold
            && _host._viewModel.Type is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 or BlockType.Heading4)
            return;

        var range = _host.GetSelectionRange() ?? _host._toolbar?.CachedSelectionRange;
        if (range == null) return;

        var (newSelStart, newSelEnd) = _host._viewModel.ApplyFormat(range.Value.start, range.Value.end, kind, color);

        _host._stateManager?.SetUpdatingFromViewModel();
        editor.Spans = _host._viewModel.Spans;
        editor.SelectionStart = newSelStart;
        editor.SelectionEnd = newSelEnd;
        editor.CaretIndex = newSelEnd;

        if (_host._stateManager != null)
            _host._stateManager.PreviousText = _host._viewModel.Content;

        _host._stateManager?.SetNormal();

        _host._toolbar?.SetCachedSelectionRange((newSelStart, newSelEnd));
        _host._toolbar?.UpdateState();
        editor.Focus();
    }

    internal void HandleSubSupShortcut(RichTextEditor editor, InlineFormatKind kind)
    {
        var blockEditor = _host.FindParentBlockEditor();
        bool hasCrossBlock = blockEditor?.HasCrossBlockTextSelection() == true;
        bool hasSelection = hasCrossBlock || _host.GetSelectionRange() != null;

        if (hasSelection)
        {
            SetStickySubSup(editor, null);
            Apply(kind, null);
            return;
        }

        var newKind = StickySubSup == kind ? (InlineFormatKind?)null : kind;
        SetStickySubSup(editor, newKind);
    }

    internal void SetStickySubSup(RichTextEditor editor, InlineFormatKind? kind)
    {
        StickySubSup = kind;
        if (kind == InlineFormatKind.Subscript)
            editor.SetPendingSubSup(true, false);
        else if (kind == InlineFormatKind.Superscript)
            editor.SetPendingSubSup(false, true);
        else if (IsCaretAdjacentToSubSupSpan(editor))
            editor.SetPendingSubSup(false, false);
        else
            editor.SetPendingSubSup(null, null);
        _host._toolbar?.UpdateState();
    }

    internal void ClearStickySubSup(RichTextEditor editor)
    {
        if (StickySubSup == null) return;
        SetStickySubSup(editor, null);
    }

    internal bool IsCaretAdjacentToSubSupSpan(RichTextEditor editor)
    {
        var spans = _host._viewModel?.Spans;
        if (spans == null) return false;
        int caret = editor.CaretIndex;

        int pos = 0;
        foreach (var span in spans)
        {
            int len = span is Core.Models.TextSpan ts ? ts.Text.Length : 1;
            int end = pos + len;

            if (span is Core.Models.TextSpan textSpan &&
                (textSpan.Style.Subscript || textSpan.Style.Superscript))
            {
                if (caret > 0 && pos < caret && end >= caret) return true;
                if (pos == caret && len > 0) return true;
            }

            pos = end;
        }
        return false;
    }

    internal bool IsCaretAtTrailingSubSupBoundary(RichTextEditor editor)
    {
        if (_host._viewModel == null) return false;
        var spans = _host._viewModel.Spans;
        int caret = editor.CaretIndex;
        if (caret <= 0) return false;

        int pos = 0;
        Core.Models.TextStyle? leftStyle = null;
        Core.Models.TextStyle? rightStyle = null;
        foreach (var span in spans)
        {
            int len = span is Core.Models.TextSpan ts ? ts.Text.Length : 1;
            int runEnd = pos + len;
            if (runEnd == caret && span is Core.Models.TextSpan lt) leftStyle = lt.Style;
            if (pos == caret && span is Core.Models.TextSpan rt) rightStyle = rt.Style;
            pos = runEnd;
        }

        if (leftStyle == null) return false;
        bool leftIsSubSup = leftStyle.Value.Subscript || leftStyle.Value.Superscript;
        bool rightIsSubSup = rightStyle?.Subscript == true || rightStyle?.Superscript == true;
        return leftIsSubSup && !rightIsSubSup;
    }

    internal void ApplyInlineEquation()
    {
        var editor = _host._currentBlockComponent?.GetRichTextEditor() ?? _host._focusManager?.GetCurrentTextBox();
        if (_host._viewModel == null || editor == null) return;

        var range = _host.GetSelectionRange() ?? _host._toolbar?.CachedSelectionRange;
        int start, end;
        if (range == null || range.Value.start >= range.Value.end)
        {
            var word = editor.TryGetWordRangeAtCaret();
            if (word == null) return;
            (start, end) = (word.Value.Start, word.Value.End);
            editor.SelectionStart = start;
            editor.SelectionEnd = end;
            editor.CaretIndex = end;
        }
        else
        {
            (start, end) = range.Value;
        }

        if (start >= end) return;

        var selectedText = InlineSpanFormatApplier.Flatten(
            InlineSpanFormatApplier.SliceRuns(_host._viewModel.Spans, start, end));
        var latex = Core.Formatting.EquationLatexNormalizer.Normalize(selectedText);

        _host._viewModel.ApplyFormat(start, end, InlineFormatKind.Equation, latex);

        editor.Spans = _host._viewModel.Spans;
        editor.CaretIndex = start + 1;
        editor.SelectionStart = start + 1;
        editor.SelectionEnd = start + 1;

        _host._toolbar?.Close();
    }
}
