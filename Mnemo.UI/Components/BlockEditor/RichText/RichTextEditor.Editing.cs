using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives.PopupPositioning;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Services.LaTeX;
using Mnemo.Infrastructure.Services.TextShortcuts;
using Mnemo.UI.Controls;
using Mnemo.UI;
using Mnemo.UI.Services;
using Mnemo.UI.Services.LaTeX.Layout.Boxes;
using Mnemo.UI.Services.LaTeX.Rendering;

namespace Mnemo.UI.Components.BlockEditor;

public partial class RichTextEditor
{
    // ── Editing operations ───────────────────────────────────────────────────

    private bool HasSelection => Math.Min(_selectionStart, _selectionEnd) < Math.Max(_selectionStart, _selectionEnd);

    private void HandleDelete()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (_caretIndex >= TextLength) return;
        DeleteRange(_caretIndex, _caretIndex + 1);
    }

    private void HandleBackspace()
    {
        if (HasSelection) { DeleteSelection(); return; }
        if (_caretIndex <= 0) return;
        if (TryUndoLastAutoShortcutConversion())
            return;
        int newCaret = _caretIndex - 1;
        DeleteRange(newCaret, _caretIndex);
        CaretIndex = newCaret;
        SelectionStart = newCaret;
        SelectionEnd = newCaret;
    }

    private void HandleBackspaceWord()
    {
        if (_caretIndex <= 0) return;
        int deleteStart = FindWordStart(_caretIndex - 1);
        if (deleteStart >= _caretIndex) return;
        DeleteRange(deleteStart, _caretIndex);
        CaretIndex = deleteStart;
        SelectionStart = deleteStart;
        SelectionEnd = deleteStart;
    }

    private void DeleteSelection()
    {
        int start = Math.Min(_selectionStart, _selectionEnd);
        int end = Math.Max(_selectionStart, _selectionEnd);
        if (start >= end) return;
        DeleteRange(start, end);
        CaretIndex = start;
        SelectionStart = start;
        SelectionEnd = start;
    }

    /// <summary>Delete characters [start, end) and notify via TextChanged.</summary>
    private void DeleteRange(int start, int end)
    {
        var flat = FlattenRuns(Spans ?? Array.Empty<InlineSpan>());
        if (flat.Length == 0 && start == 0 && end == 1)
        {
            SelectionStart = 0;
            SelectionEnd = 0;
            CaretIndex = 0;
            return;
        }
        var runs = ApplyTextDeletion(Spans ?? Array.Empty<InlineSpan>(), start, end);
        Spans = runs;
    }

    private void InsertText(string text)
    {
        int start = Math.Min(_selectionStart, _selectionEnd);
        int end = Math.Max(_selectionStart, _selectionEnd);
        var currentRuns = Spans ?? Array.Empty<InlineSpan>();
        var oldFlat = FlattenRuns(currentRuns);
        var maxIndex = oldFlat.Length;
        start = Math.Clamp(start, 0, maxIndex);
        end = Math.Clamp(end, 0, maxIndex);
        if (end < start)
            end = start;
        int removeLen = end - start;
        var newFlat = removeLen > 0
            ? oldFlat.Remove(start, removeLen).Insert(start, text)
            : oldFlat.Insert(start, text);
        int newCaret = start + text.Length;

        var shortcuts = _services?.Shortcuts;
        if (shortcuts != null)
        {
            var result = shortcuts.Apply(newFlat, newCaret, start, text.Length);
            if (result.WasTransformed)
            {
                newFlat = result.Text;
                newCaret = Math.Clamp(result.CaretIndex, 0, newFlat.Length);
                if (!string.IsNullOrEmpty(result.LastAppliedSequence)
                    && !string.IsNullOrEmpty(result.LastAppliedReplacement)
                    && result.LastAppliedStartIndex >= 0)
                {
                    _pendingAutoShortcutUndo = new AutoShortcutUndoState
                    {
                        ReplacementStart = result.LastAppliedStartIndex,
                        Replacement = result.LastAppliedReplacement!,
                        OriginalSequence = result.LastAppliedSequence!,
                        TextLengthAfterConversion = newFlat.Length
                    };
                }
            }
            else
                _pendingAutoShortcutUndo = null;
        }
        else
            _pendingAutoShortcutUndo = null;

        IReadOnlyList<InlineSpan> runs = Core.Formatting.InlineSpanFormatApplier.ApplyTextEdit(currentRuns, oldFlat, newFlat);

        // Sticky sub/sup mode: force the desired flags on the inserted character range.
        if (_pendingInsertStyle is { } pending && text.Length > 0 && newCaret > start)
        {
            runs = Core.Formatting.InlineSpanFormatApplier.ForceSubSup(
                runs, start, newCaret, pending.Subscript, pending.Superscript);
            if (_clearPendingInsertStyleAfterNextInsert)
            {
                _pendingInsertStyle = null;
                _clearPendingInsertStyleAfterNextInsert = false;
            }
        }

        if (TryPromoteFractionAtCaret(runs, newCaret, out var fractionRuns, out var fractionCaret))
        {
            runs = fractionRuns;
            newCaret = fractionCaret;
        }
        Spans = runs;
        CaretIndex = newCaret;
        SelectionStart = newCaret;
        SelectionEnd = newCaret;
    }

    private void ScheduleSpellcheck(bool force = false) => _spellcheck?.Schedule(force);

    private bool SpellcheckDecorationsActive => _spellcheck?.DecorationsActive ?? false;

    private void OnSpellcheckDecorationGateChanged() => _spellcheck?.OnDecorationGateChanged();

    private void RebuildSpellcheckGeometry()
        => _spellcheck?.RebuildGeometry(_textLayout, _layoutBoundaryAtLogical, _layoutTextLength, TextLength);

    private void RenderSpellcheckUnderlines(DrawingContext context)
    {
        var sc = _spellcheck;
        if (sc == null || !sc.DecorationsActive || _textLayout == null) return;

        var lines = sc.UnderlineLines;
        if (lines == null)
        {
            sc.RebuildGeometry(_textLayout, _layoutBoundaryAtLogical, _layoutTextLength, TextLength);
            lines = sc.UnderlineLines;
        }
        if (lines == null || lines.Count == 0) return;

        var pen = sc.GetOrCreatePen();
        foreach (var (from, to) in lines)
            context.DrawLine(pen, from, to);
    }

    private bool TryOpenSpellcheckContextMenu(Point point)
    {
        var sc = _spellcheck;
        if (sc == null || !sc.DecorationsActive) return false;
        var idx = HitTestPoint(point);
        if (!sc.TryGetIssueAtIndex(idx, out var issue)) return false;
        _ = sc.SuggestAsync(issue);
        return true;
    }

    private static bool TryPromoteFractionAtCaret(
        IReadOnlyList<InlineSpan> runs,
        int caretIndex,
        out IReadOnlyList<InlineSpan> promotedRuns,
        out int promotedCaret)
    {
        promotedRuns = runs;
        promotedCaret = caretIndex;

        var flat = InlineSpanText.FlattenEditing(runs);
        if (caretIndex <= 0 || caretIndex > flat.Length)
            return false;

        var windowStart = Math.Max(0, caretIndex - 24);
        var tail = flat.Substring(windowStart, caretIndex - windowStart);
        var match = TailFractionTokenRegex.Match(tail);
        if (!match.Success)
            return false;

        var token = match.Value;
        if (!FractionShortcutResolver.TryParse(token, out var numerator, out var denominator))
            return false;

        var tokenStart = windowStart + match.Index;
        var tokenEnd = tokenStart + token.Length;
        var insertion = new List<InlineSpan> { new FractionSpan(numerator, denominator) };
        promotedRuns = Core.Formatting.InlineSpanFormatApplier.ReplaceRange(runs, tokenStart, tokenEnd, insertion);
        promotedCaret = tokenStart + 1;
        return true;
    }

    private bool TryUndoLastAutoShortcutConversion()
    {
        var pending = _pendingAutoShortcutUndo;
        if (pending is null)
            return false;

        if (_caretIndex != pending.ReplacementStart + pending.Replacement.Length
            || TextLength != pending.TextLengthAfterConversion
            || pending.ReplacementStart < 0
            || pending.ReplacementStart + pending.Replacement.Length > TextLength)
        {
            _pendingAutoShortcutUndo = null;
            return false;
        }

        var currentRuns = Spans ?? Array.Empty<InlineSpan>();
        var oldFlat = FlattenRuns(currentRuns);
        if (!oldFlat.AsSpan(pending.ReplacementStart, pending.Replacement.Length).SequenceEqual(pending.Replacement.AsSpan()))
        {
            _pendingAutoShortcutUndo = null;
            return false;
        }

        var newFlat = oldFlat.Remove(pending.ReplacementStart, pending.Replacement.Length)
            .Insert(pending.ReplacementStart, pending.OriginalSequence);
        var newRuns = Core.Formatting.InlineSpanFormatApplier.ApplyTextEdit(currentRuns, oldFlat, newFlat);
        var newCaret = pending.ReplacementStart + pending.OriginalSequence.Length;

        Spans = newRuns;
        CaretIndex = newCaret;
        SelectionStart = newCaret;
        SelectionEnd = newCaret;
        _pendingAutoShortcutUndo = null;
        return true;
    }

    /// <summary>Inserts text at the caret (replacing selection), same as typed input.</summary>
    public void InsertTextAtCaret(string text) => InsertText(text);

    /// <summary>
    /// Activates sticky sub/sup typing mode: subsequent insertions are forced to
    /// <paramref name="subscript"/>/<paramref name="superscript"/> regardless of adjacent span style.
    /// Pass both <c>false</c> to escape from an active sub/sup span (disables one insertion then auto-clears).
    /// Pass <c>null</c> to fully disable the override and resume natural style inheritance.
    /// </summary>
    internal void SetPendingSubSup(bool? subscript, bool? superscript)
    {
        if (subscript == null && superscript == null)
        {
            _pendingInsertStyle = null;
            _clearPendingInsertStyleAfterNextInsert = false;
            InvalidateVisual();
            return;
        }

        bool sub = subscript ?? false;
        bool sup = superscript ?? false;

        if (!sub && !sup)
        {
            // Escape mode: force-clear sub/sup for exactly one insertion, then return to natural.
            _pendingInsertStyle = Core.Models.TextStyle.Default;
            _clearPendingInsertStyleAfterNextInsert = true;
            InvalidateVisual();
        }
        else
        {
            _pendingInsertStyle = new Core.Models.TextStyle(Subscript: sub, Superscript: sup);
            _clearPendingInsertStyleAfterNextInsert = false;
            InvalidateVisual();
        }
    }

    // ── Run mutation helpers ─────────────────────────────────────────────────

    private static IReadOnlyList<InlineSpan> ApplyTextDeletion(
        IReadOnlyList<InlineSpan> runs, int start, int end)
    {
        return Core.Formatting.InlineSpanFormatApplier.ApplyTextEdit(
            runs, FlattenRuns(runs), FlattenRuns(runs).Remove(start, end - start));
    }

    private static IReadOnlyList<InlineSpan> ApplyTextInsertion(
        IReadOnlyList<InlineSpan> runs, int selStart, int selEnd, string text)
    {
        var flat = FlattenRuns(runs);
        int removeLen = selEnd - selStart;
        var newFlat = removeLen > 0
            ? flat.Remove(selStart, removeLen).Insert(selStart, text)
            : flat.Insert(selStart, text);
        return Core.Formatting.InlineSpanFormatApplier.ApplyTextEdit(runs, flat, newFlat);
    }

    private static string FlattenRuns(IReadOnlyList<InlineSpan> runs) =>
        InlineSpanText.FlattenEditing(runs);

}