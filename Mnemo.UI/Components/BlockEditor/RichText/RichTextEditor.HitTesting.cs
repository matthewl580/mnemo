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
using Microsoft.Extensions.DependencyInjection;
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
    // ── Hit-testing ──────────────────────────────────────────────────────────

    /// <summary>Returns true if <paramref name="index"/> falls inside a linked run; outputs the href.</summary>
    public bool TryGetLinkUrlAt(int index, out string? url)
    {
        url = null;
        var runs = Spans ?? Array.Empty<InlineSpan>();
        if (runs.Count == 0) return false;
        int len = TextLength;
        if (len == 0) return false;
        int i = Math.Clamp(index, 0, len - 1);
        int pos = 0;
        foreach (var seg in runs)
        {
            int segLen = seg is TextSpan t ? t.Text.Length : 1;
            int end = pos + segLen;
            if (i < end && i >= pos)
            {
                url = seg switch
                {
                    TextSpan tx => tx.Style.LinkUrl,
                    EquationSpan eq => eq.Style.LinkUrl,
                    FractionSpan fr => fr.Style.LinkUrl,
                    _ => null
                };
                return url != null;
            }

            pos = end;
        }

        return false;
    }

    /// <summary>Opens http(s) or mailto in the system browser / mail client.</summary>
    public static void OpenExternalUrl(string url, Control? anchor)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            var top = anchor != null ? TopLevel.GetTopLevel(anchor) : null;
            if (top?.Launcher != null)
            {
                _ = top.Launcher.LaunchUriAsync(uri);
                return;
            }
        }
        catch
        {
            // fall through to shell
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    public int HitTestPoint(Point point)
    {
        // Reuse existing width-sync guard so pointer hit-tests use a layout matching rendered width.
        EnsureLayoutForVerticalNavigation();

        // No layout yet (just realized / rebuild in flight): keep the current caret rather than
        // reporting index 0, which would snap the caret and scroll position to the top.
        if (_textLayout is null) return Math.Clamp(_caretIndex, 0, TextLength);
        var local = new Point(point.X - _mathPadLeft, point.Y - _mathPadTop);
        if (TryClampHitToLineEdge(local, out var clamped))
            return clamped;
        try
        {
            var result = _textLayout.HitTestPoint(local);
            int rawLayoutBoundary = result.IsTrailing ? result.TextPosition + 1 : result.TextPosition;
            int layoutBoundary = Math.Clamp(rawLayoutBoundary, 0, _layoutTextLength);
            int logical = LayoutBoundaryIndexToLogicalCaret(layoutBoundary);
            return Math.Clamp(logical, 0, TextLength);
        }
        catch
        {
            return HitTestPointFallback(local);
        }
    }

    /// <summary>
    /// If the pointer is clearly to the left/right of a visual line, clamp to the line's start/end caret.
    /// This avoids relying on <see cref="TextLayout.HitTestPoint"/> behaviour for far-off whitespace (which can vary
    /// and may return a non-edge character without throwing).
    /// </summary>
    private bool TryClampHitToLineEdge(Point local, out int logicalCaret)
    {
        if (_textLayout == null || _layoutBoundaryAtLogical == null)
        {
            logicalCaret = 0;
            return false;
        }
        return RichTextHitTester.TryClampHitToLineEdge(_textLayout, _layoutBoundaryAtLogical, _layoutTextLength, TextLength, local, out logicalCaret);
    }

    private static (int LineIndex, double LineTop) GetLineIndexAtY(IReadOnlyList<TextLine> lines, double y)
        => RichTextHitTester.GetLineIndexAtY(lines, y);

    private int HitTestPointFallback(Point local)
    {
        if (_textLayout == null || _layoutBoundaryAtLogical == null) return 0;
        return RichTextHitTester.HitTestPointFallback(_textLayout, _layoutBoundaryAtLogical, _layoutTextLength, TextLength, local);
    }

    private (double MinX, double MaxX) GetLineHorizontalHitBounds(TextLine line, int lineStart, int lineEnd)
    {
        var minX = 0.0;
        var maxX = line.WidthIncludingTrailingWhitespace;
        if (double.IsNaN(maxX) || double.IsInfinity(maxX) || maxX <= 0)
            maxX = Math.Max(line.Width, 0);
        if (_textLayout == null) return (minX, maxX);
        try
        {
            var startRect = _textLayout.HitTestTextPosition(Math.Clamp(lineStart, 0, _layoutTextLength));
            minX = startRect.X;
            maxX = minX + Math.Max(maxX, 0);
        }
        catch { }
        return (minX, maxX);
    }

    /// <summary>
    /// Union of layout span rects and painted math ink, in text-layout local coordinates (inside math padding transform).
    /// </summary>
    private bool TryGetInlineEquationBounds(InlineEquationEntry eq, double pad, out Rect bounds)
    {
        bounds = default;
        if (_textLayout == null || _layoutBoundaryAtLogical == null)
            return false;

        int lo = LogicalCaretToLayoutBoundary(eq.CharIndex);
        int hi = LogicalCaretToLayoutBoundary(eq.CharIndex + 1);
        int spanLen = hi - lo;
        if (spanLen <= 0)
            return false;

        try
        {
            var rects = _textLayout.HitTestTextRange(lo, spanLen).ToList();
            if (rects.Count == 0)
            {
                var p = _textLayout.HitTestTextPosition(lo);
                bounds = new Rect(p.X, p.Y, Math.Max(p.Width, 1), p.Height);
            }
            else
            {
                bounds = rects[0];
                for (int i = 1; i < rects.Count; i++)
                    bounds = bounds.Union(rects[i]);
            }
        }
        catch
        {
            return false;
        }

        if (eq.Layout != null)
        {
            try
            {
                var leftX = _textLayout.HitTestTextPosition(lo).X;
                var baselineY = GetTextBaselineYForLayoutIndex(_textLayout, lo);
                var ink = CalculateMathPaintBounds(eq.Layout, leftX, baselineY);
                bounds = bounds.Union(ink);
            }
            catch
            {
                // keep layout-only bounds
            }
        }

        bounds = new Rect(bounds.X - pad, bounds.Y - pad, bounds.Width + 2 * pad, bounds.Height + 2 * pad);
        return bounds.Width > 0 && bounds.Height > 0;
    }

    /// <summary>
    /// Notion-like: hit target is the union of the reserved layout span and the painted math ink (with padding),
    /// not only layout character boundaries (which missed trailing space and tall fractions).
    /// </summary>
    private bool TryOpenInlineEquationFlyoutAtPoint(Point pos)
    {
        if (_textLayout == null || _layoutBoundaryAtLogical == null || _equations == null)
            return false;

        var local = new Point(pos.X - _mathPadLeft, pos.Y - _mathPadTop);

        foreach (var eq in _equations.Entries)
        {
            const double pad = 3;
            if (!TryGetInlineEquationBounds(eq, pad, out var bounds))
                continue;

            if (bounds.Contains(local))
            {
                _equations.OpenFlyout(eq.CharIndex, eq.Latex ?? string.Empty, _textLayout, _layoutTextLength);
                return true;
            }
        }

        return false;
    }

    private void UpdateInlineEquationHoverState(Point pos)
    {
        var eq = _equations;
        if (!ShowInlineEquationSourceOnHover || _textLayout == null || _layoutBoundaryAtLogical == null || eq == null)
        {
            eq?.SetHovered(null);
            return;
        }

        var local = new Point(pos.X - _mathPadLeft, pos.Y - _mathPadTop);
        int? hovered = null;
        foreach (var entry in eq.Entries)
        {
            if (TryGetInlineEquationBounds(entry, pad: 2, out var bounds) && bounds.Contains(local))
            {
                hovered = entry.CharIndex;
                break;
            }
        }

        eq.SetHovered(hovered);
    }

    // ── Word nav helpers ─────────────────────────────────────────────────────

    private void MoveOrExtend(bool extend, int newPos)
    {
        if (extend)
        {
            SelectionEnd = newPos;
            CaretIndex = newPos;
        }
        else
        {
            if (HasSelection && !extend)
            {
                // Collapse to the near end of the selection
                int sel = newPos < _caretIndex
                    ? Math.Min(_selectionStart, _selectionEnd)
                    : Math.Max(_selectionStart, _selectionEnd);
                CaretIndex = sel;
            }
            else
            {
                CaretIndex = newPos;
            }
            SelectionStart = CaretIndex;
            SelectionEnd = CaretIndex;
        }
    }

    /// <summary>
    /// Up/Down to the adjacent <see cref="TextLayout"/> line (soft wrap and hard newlines).
    /// Returns false on the first/last visual line so the block editor can move focus.
    /// </summary>
    public bool TryVerticalLogicalNavigation(bool shift, bool up)
    {
        if (!TryMoveCaretOneVisualLine(up, out var newPos))
            return false;
        MoveOrExtend(shift, newPos);
        ResetCaretBlink();
        return true;
    }

    /// <summary>
    /// Horizontal center of the caret in layout coordinates, for matching column when moving to another block.
    /// </summary>
    public bool TryGetCaretHorizontalOffsetForBlockNavigation(out double pixelX)
    {
        pixelX = 0;
        EnsureLayoutForVerticalNavigation();
        var layout = _textLayout;
        if (layout == null)
            return false;

        if (TextLength == 0)
        {
            try
            {
                var r = layout.HitTestTextPosition(0);
                pixelX = r.Width > 0.01 ? r.X + r.Width * 0.5 : r.X + 1;
            }
            catch
            {
                pixelX = 0;
            }
            return true;
        }

        var hitPos = LogicalCaretToLayoutBoundary(Math.Clamp(_caretIndex, 0, TextLength));
        try
        {
            var caretRect = layout.HitTestTextPosition(hitPos);
            pixelX = caretRect.Width > 0.01
                ? caretRect.X + caretRect.Width * 0.5
                : caretRect.X + 1;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Places the caret on the first or last visual line at the given horizontal offset (same convention as
    /// <see cref="TryGetCaretHorizontalOffsetForBlockNavigation"/>).
    /// </summary>
    public int GetCaretIndexFromHorizontalOffset(double pixelX, bool useFirstVisualLine)
    {
        EnsureLayoutForVerticalNavigation();
        var layout = _textLayout;
        if (layout == null)
            return Math.Clamp(_caretIndex, 0, TextLength);

        var lines = layout.TextLines;
        if (lines.Count == 0)
            return Math.Clamp(_caretIndex, 0, TextLength);

        int lineIdx = useFirstVisualLine ? 0 : lines.Count - 1;
        var targetTextLine = lines[lineIdx];
        var yTop = GetAccumulatedLineTop(layout, lineIdx);
        var probeY = yTop + targetTextLine.Height * 0.5;
        var maxX = Math.Max(targetTextLine.WidthIncludingTrailingWhitespace, 1);
        var probeX = Math.Clamp(pixelX, 0, maxX);
        var idx = HitTestLayoutAt(layout, new Point(probeX, probeY));
        return Math.Clamp(idx, 0, TextLength);
    }

    private void EnsureLayoutForVerticalNavigation()
    {
        var effectiveW = ComputeEffectiveLayoutMaxWidth();
        var needRebuild = _textLayout == null || Text != _lastBuiltText;
        if (!needRebuild && _textLayout != null
            && Math.Abs(effectiveW - _textLayout.MaxWidth) > 0.5)
            needRebuild = true;
        if (needRebuild)
            BuildLayout(effectiveW);
    }

    /// <summary>
    /// Moves the caret to the same horizontal aim on the adjacent visual line.
    /// </summary>
    private bool TryMoveCaretOneVisualLine(bool up, out int newCaretIndex)
    {
        newCaretIndex = _caretIndex;
        EnsureLayoutForVerticalNavigation();
        var layout = _textLayout;
        if (layout == null)
            return false;

        var lines = layout.TextLines;
        if (lines.Count == 0 || TextLength == 0)
            return false;

        var trailingEdge = _caretIndex >= TextLength;
        var idxForLineLookup = LogicalCaretToLayoutBoundary(Math.Clamp(_caretIndex, 0, TextLength));
        var oldLine = layout.GetLineIndexFromCharacterIndex(idxForLineLookup, trailingEdge);

        if (up)
        {
            if (oldLine <= 0)
                return false;
        }
        else if (oldLine >= lines.Count - 1)
        {
            return false;
        }

        var targetVisualLine = up ? oldLine - 1 : oldLine + 1;
        var targetTextLine = lines[targetVisualLine];
        var yTop = GetAccumulatedLineTop(layout, targetVisualLine);
        var probeY = yTop + targetTextLine.Height * 0.5;

        var hitPos = LogicalCaretToLayoutBoundary(Math.Clamp(_caretIndex, 0, TextLength));
        Rect caretRect;
        try
        {
            caretRect = layout.HitTestTextPosition(hitPos);
        }
        catch
        {
            caretRect = default;
        }

        var probeX = caretRect.Width > 0.01
            ? caretRect.X + caretRect.Width * 0.5
            : caretRect.X + 1;
        var maxX = Math.Max(targetTextLine.WidthIncludingTrailingWhitespace, 1);
        probeX = Math.Clamp(probeX, 0, maxX);

        newCaretIndex = HitTestLayoutAt(layout, new Point(probeX, probeY));

        var layoutIdxForNewLine = LogicalCaretToLayoutBoundary(Math.Clamp(newCaretIndex, 0, TextLength));
        var newLine = layout.GetLineIndexFromCharacterIndex(layoutIdxForNewLine, newCaretIndex >= TextLength);

        if (newLine != targetVisualLine)
        {
            var layoutCaretOld = LogicalCaretToLayoutBoundary(Math.Clamp(_caretIndex, 0, TextLength));
            var col = Math.Max(0, layoutCaretOld - lines[oldLine].FirstTextSourceIndex);
            var layoutCaret = FallbackCaretSameColumn(lines[targetVisualLine], col);
            newCaretIndex = LayoutBoundaryIndexToLogicalCaret(Math.Clamp(layoutCaret, 0, _layoutTextLength));
        }

        newCaretIndex = Math.Clamp(newCaretIndex, 0, TextLength);
        return true;
    }

    private static double GetAccumulatedLineTop(TextLayout layout, int lineIndex)
        => RichTextHitTester.GetAccumulatedLineTop(layout, lineIndex);

    private int HitTestLayoutAt(TextLayout layout, Point point)
    {
        try
        {
            var result = layout.HitTestPoint(point);
            int layoutBoundary = result.IsTrailing ? result.TextPosition + 1 : result.TextPosition;
            layoutBoundary = Math.Clamp(layoutBoundary, 0, _layoutTextLength);
            return Math.Clamp(LayoutBoundaryIndexToLogicalCaret(layoutBoundary), 0, TextLength);
        }
        catch
        {
            return _caretIndex;
        }
    }

    private static int LineContentCharCount(TextLine line) => RichTextHitTester.LineContentCharCount(line);

    private static int FallbackCaretSameColumn(TextLine line, int columnFromLineStart)
        => RichTextHitTester.FallbackCaretSameColumn(line, columnFromLineStart);

    private static bool IsEquationPlaceholderChar(char c) =>
        c == InlineSpan.EquationAtomChar || c == InlineSpan.FractionAtomChar;

    private int FindWordStart(int pos)
    {
        var text = Text;
        pos = Math.Clamp(pos, 0, text.Length);
        if (pos < text.Length && IsEquationPlaceholderChar(text[pos]))
            return pos;
        if (pos > 0 && IsEquationPlaceholderChar(text[pos - 1]))
            return pos - 1;
        while (pos > 0 && !char.IsWhiteSpace(text[pos - 1]))
        {
            if (IsEquationPlaceholderChar(text[pos - 1]))
                return pos - 1;
            pos--;
        }

        return pos;
    }

    private int FindWordEnd(int pos)
    {
        var text = Text;
        pos = Math.Clamp(pos, 0, text.Length);
        if (pos < text.Length && IsEquationPlaceholderChar(text[pos]))
            return pos + 1;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]))
        {
            if (IsEquationPlaceholderChar(text[pos]))
                return pos + 1;
            pos++;
        }

        return pos;
    }

    /// <summary>
    /// Non-empty [start, end) span of the word at the caret, or null if the caret is on whitespace only.
    /// Used for format shortcuts when there is no active selection.
    /// </summary>
    public (int Start, int End)? TryGetWordRangeAtCaret()
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0) return null;

        int idx = Math.Clamp(_caretIndex, 0, text.Length);

        if (idx < text.Length && char.IsWhiteSpace(text[idx]))
            return null;

        if (idx == text.Length)
        {
            if (!char.IsWhiteSpace(text[idx - 1]))
                idx = idx - 1;
            else
                return null;
        }

        int start = FindWordStart(idx);
        int end = FindWordEnd(idx);
        if (start >= end) return null;
        return (start, end);
    }

    private void SelectWord(int pos)
    {
        SelectionStart = FindWordStart(pos);
        SelectionEnd = FindWordEnd(pos);
        CaretIndex = SelectionEnd;
    }

}