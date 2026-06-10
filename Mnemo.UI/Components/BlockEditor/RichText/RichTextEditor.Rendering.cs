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
    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        // Rebuild layout only if null or content changed. Use _lastLayoutWidth to avoid building with Bounds (can be 0 during first frame) which can cause layout loops.
        var currentText = Text;
        var layoutWidth = _lastLayoutWidth > 0 ? _lastLayoutWidth : (Bounds.Width > 0 ? Bounds.Width : MinLayoutWidth);
        if (_textLayout == null || currentText != _lastBuiltText)
            BuildLayout(layoutWidth);

        var origin = new Point(0, 0);

        using (context.PushTransform(Matrix.CreateTranslation(_mathPadLeft, _mathPadTop)))
        {
            int selStart = Math.Min(_selectionStart, _selectionEnd);
            int selEnd = Math.Max(_selectionStart, _selectionEnd);

            // 1. Run backgrounds only.
            if (_backgroundLayout != null)
                _backgroundLayout.Draw(context, origin);

            // 2. Search highlights above run backgrounds, but below text selection.
            RenderSearchHighlights(context);

            // 3. Selection overlay above run/search highlights.
            if (selEnd > selStart && _textLayout != null)
            {
                var selBrush = SelectionBrush ?? GetThemeSelectionBrush();
                if (string.IsNullOrEmpty(Text))
                {
                    double h = _textLayout.Height > 0 ? _textLayout.Height : FontSize;
                    double w = Math.Max(3.0, FontSize * 0.45);
                    context.FillRectangle(selBrush, new Rect(0, 0, w, h));
                }
                else
                {
                    int layoutSelStart = LogicalCaretToLayoutBoundary(selStart);
                    int layoutSelLen = LogicalCaretToLayoutBoundary(selEnd) - layoutSelStart;
                    // Avalonia HitTestTextRange requires non-zero length; mapping can be 0 on edge transitions.
                    List<Rect> rects;
                    if (layoutSelLen <= 0)
                        rects = new List<Rect>();
                    else
                        rects = _textLayout.HitTestTextRange(layoutSelStart, layoutSelLen).ToList();

                    bool hasDrawable = rects.Any(r => r.Width > 0.5 && r.Height > 0.5);
                    if (!hasDrawable)
                    {
                        // U+200B and other zero-advance glyphs often yield no range rects; draw a caret-sized chip.
                        try
                        {
                            int idx = TextLength > 0 ? LogicalCaretToLayoutBoundary(Math.Clamp(selStart, 0, TextLength - 1)) : 0;
                            var pos = _textLayout.HitTestTextPosition(idx);
                            double h = pos.Height > 0 ? pos.Height : (_textLayout.Height > 0 ? _textLayout.Height : FontSize);
                            double chipW = pos.Width > 0.5 ? pos.Width : Math.Max(3.0, FontSize * 0.45);
                            context.FillRectangle(selBrush, new Rect(pos.X, pos.Y, chipW, h));
                        }
                        catch
                        {
                            double h = _textLayout.Height > 0 ? _textLayout.Height : FontSize;
                            double chipW = Math.Max(3.0, FontSize * 0.45);
                            context.FillRectangle(selBrush, new Rect(0, 0, chipW, h));
                        }
                    }
                    else
                    {
                        foreach (var rect in rects)
                            context.FillRectangle(selBrush, rect.Translate(origin));
                    }
                }
            }

            // 4. Foreground glyphs and inline math.
            if (_textLayout != null)
            {
                _textLayout.Draw(context, origin);
                RenderSpellcheckUnderlines(context);
                RenderInlineEquations(context);
                if (ShouldDrawWatermark() && _watermarkLayout != null)
                    _watermarkLayout.Draw(context, origin);
            }
            // 5. Caret on top.
            if (IsFocused && _caretVisible && selEnd == selStart && _textLayout != null)
            {
                var caretBrush = CaretBrush ?? GetThemeCaretBrush();
                var caretRect = GetCaretRect();
                context.FillRectangle(caretBrush, caretRect);
            }

        }

        if (perfStart != 0)
        {
            var selLen = Math.Abs(_selectionEnd - _selectionStart);
            EditorPerfDiagnostics.ReportInteraction(
                perf,
                "richText.render",
                EditorPerfDiagnostics.ElapsedMs(perfStart),
                $"text={TextLength} sel={selLen} highlights={SearchHighlightRanges?.Count ?? 0}");
        }
    }

    private void RenderSearchHighlights(DrawingContext context) =>
        RichTextSearchHighlightRenderer.Render(
            context,
            _textLayout,
            TextLength,
            SearchHighlightRanges,
            ActiveSearchHighlightRange,
            LogicalCaretToLayoutBoundary);

    /// <summary>Bounding rect of the first text line in local coordinates (Y/Height match the line box; width is at least one glyph wide).</summary>
    public Rect GetFirstLineBounds()
    {
        var layoutWidth = Bounds.Width > 0 ? Bounds.Width : (_lastLayoutWidth > 0 ? _lastLayoutWidth : MinLayoutWidth);
        if (layoutWidth <= 0 || double.IsNaN(layoutWidth))
            layoutWidth = MinLayoutWidth;
        var currentText = Text;
        if (_textLayout == null || currentText != _lastBuiltText)
            BuildLayout(layoutWidth);

        if (_textLayout != null && !string.IsNullOrEmpty(currentText))
        {
            try
            {
                var charRect = _textLayout.HitTestTextPosition(0);
                var h = charRect.Height > 0 ? charRect.Height : FontSize;
                var w = charRect.Width > 0 ? charRect.Width : 1;
                return new Rect(charRect.X + _mathPadLeft, charRect.Y + _mathPadTop, w, h);
            }
            catch
            {
                // fall through
            }
        }

        if (ShouldDrawWatermark() && _watermarkLayout != null)
        {
            try
            {
                var charRect = _watermarkLayout.HitTestTextPosition(0);
                var h = charRect.Height > 0 ? charRect.Height : FontSize;
                var w = charRect.Width > 0 ? charRect.Width : 1;
                return new Rect(charRect.X + _mathPadLeft, charRect.Y + _mathPadTop, w, h);
            }
            catch
            {
                // fall through
            }
        }

        return new Rect(_mathPadLeft, _mathPadTop, Bounds.Width > 0 ? Bounds.Width : layoutWidth, FontSize);
    }

    /// <summary>Returns the bounding rect of the current selection in local coordinates, or null if no selection or layout not ready.</summary>
    public Rect? GetSelectionBounds()
    {
        int selStart = Math.Min(_selectionStart, _selectionEnd);
        int selEnd = Math.Max(_selectionStart, _selectionEnd);
        if (selEnd <= selStart || _textLayout == null) return null;
        if (string.IsNullOrEmpty(Text))
        {
            double h = _textLayout.Height > 0 ? _textLayout.Height : FontSize;
            double w = Math.Max(3.0, FontSize * 0.45);
            return new Rect(_mathPadLeft, _mathPadTop, w, h);
        }
        try
        {
            int layoutSelStart = LogicalCaretToLayoutBoundary(selStart);
            int layoutSelLen = LogicalCaretToLayoutBoundary(selEnd) - layoutSelStart;
            if (layoutSelLen <= 0)
            {
                int fbIdx = TextLength > 0 ? LogicalCaretToLayoutBoundary(Math.Clamp(selStart, 0, TextLength - 1)) : 0;
                var fbPos = _textLayout.HitTestTextPosition(fbIdx);
                double fbH = fbPos.Height > 0 ? fbPos.Height : (_textLayout.Height > 0 ? _textLayout.Height : FontSize);
                double fbW = fbPos.Width > 0.5 ? fbPos.Width : Math.Max(3.0, FontSize * 0.45);
                return new Rect(fbPos.X + _mathPadLeft, fbPos.Y + _mathPadTop, fbW, fbH);
            }

            var rects = _textLayout.HitTestTextRange(layoutSelStart, layoutSelLen).ToList();
            bool hasDrawable = rects.Any(r => r.Width > 0.5 && r.Height > 0.5);
            if (hasDrawable)
            {
                var r = rects[0];
                for (int i = 1; i < rects.Count; i++)
                    r = r.Union(rects[i]);
                return r.Translate(new Vector(_mathPadLeft, _mathPadTop));
            }
            int idx = TextLength > 0 ? LogicalCaretToLayoutBoundary(Math.Clamp(selStart, 0, TextLength - 1)) : 0;
            var pos = _textLayout.HitTestTextPosition(idx);
            double h = pos.Height > 0 ? pos.Height : (_textLayout.Height > 0 ? _textLayout.Height : FontSize);
            double chipW = pos.Width > 0.5 ? pos.Width : Math.Max(3.0, FontSize * 0.45);
            return new Rect(pos.X + _mathPadLeft, pos.Y + _mathPadTop, chipW, h);
        }
        catch
        {
            return null;
        }
    }

    private Rect GetCaretRect()
    {
        if (_textLayout == null) return new Rect(0, 0, 1, FontSize);
        try
        {
            var layoutIdx = LogicalCaretToLayoutBoundary(_caretIndex);
            var charRect = _textLayout.HitTestTextPosition(layoutIdx);
            double x = charRect.X;
            double lineH = charRect.Height > 0 ? charRect.Height : FontSize;
            double lineY = charRect.Y;

            var (isSub, isSup) = GetCaretSubSupStyle();
            if (isSub || isSup)
            {
                var (subY, subH) = GetSubSupVerticalMetrics(lineY, lineH, isSub, isSup);
                return new Rect(x, subY, 1.5, subH);
            }
            return new Rect(x, lineY, 1.5, lineH);
        }
        catch
        {
            return new Rect(0, 0, 1.5, FontSize);
        }
    }

    /// <summary>
    /// Returns whether the caret should render as subscript or superscript.
    /// <para>
    /// When a pending insert style is active (sticky mode or escape mode) it takes full priority,
    /// so the caret immediately reflects the typing mode and correctly shows normal height even right
    /// after exiting sticky mode while the caret is still inside a sub/sup span.
    /// </para>
    /// <para>
    /// When no pending style is active (plain navigation), the style of the character immediately
    /// to the left of the caret is used, matching the visual expected from the next keystroke.
    /// </para>
    /// </summary>
    private (bool sub, bool sup) GetCaretSubSupStyle()
    {
        if (_pendingInsertStyle.HasValue)
            return (_pendingInsertStyle.Value.Subscript, _pendingInsertStyle.Value.Superscript);

        // Navigation: reflect the style of the character to the left of the caret.
        if (_caretIndex <= 0) return (false, false);
        var spans = Spans;
        int pos = 0;
        foreach (var span in spans)
        {
            int len = span is Core.Models.TextSpan ts ? ts.Text.Length : 1;
            int end = pos + len;
            if (pos < _caretIndex && end >= _caretIndex)
            {
                if (span is Core.Models.TextSpan textSpan)
                    return (textSpan.Style.Subscript, textSpan.Style.Superscript);
                return (false, false);
            }
            pos = end;
        }
        return (false, false);
    }

    /// <summary>
    /// Computes the Y offset and height for a sub/sup caret or bracket,
    /// preferring live glyph metrics from the text layout when an actual sub/sup character exists.
    /// </summary>
    private (double y, double height) GetSubSupVerticalMetrics(double lineY, double lineH, bool isSub, bool isSup)
    {
        double subH = FontSize * SubSuperscriptFontSizeRatio;

        // Try to sample actual glyph metrics from an existing sub/sup run in the layout.
        if (_textLayout != null)
        {
            var spans = Spans;
            int pos = 0;
            foreach (var span in spans)
            {
                if (span is Core.Models.TextSpan ts && ts.Text.Length > 0)
                {
                    bool match = (isSub && ts.Style.Subscript) || (isSup && ts.Style.Superscript);
                    if (match)
                    {
                        try
                        {
                            var idx = LogicalCaretToLayoutBoundary(pos);
                            var rect = _textLayout.HitTestTextPosition(idx);
                            if (rect.Height > 0 && rect.Height < lineH - 0.5)
                                return (rect.Y, rect.Height);
                        }
                        catch { }
                    }
                    pos += ts.Text.Length;
                }
                else pos++;
            }
        }

        // Fallback: approximate from line proportions.
        if (isSup)
            return (lineY, subH);
        return (lineY + lineH - subH, subH);
    }

    // ── Caret timer ──────────────────────────────────────────────────────────

    private void StartCaretTimer()
    {
        if (_caretTimer != null) return;
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
        _caretTimer.Start();
    }

    private void StopCaretTimer()
    {
        _caretTimer?.Stop();
        _caretTimer = null;
    }

    private void ResetCaretBlink()
    {
        _caretVisible = true;
        _caretTimer?.Stop();
        _caretTimer?.Start();
        InvalidateVisual();
    }

    /// <summary>
    /// Full-bounds hit target: plain <see cref="Control"/> does not hit-test “empty” space, so clicks would pass through.
    /// </summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    // ── Focus ────────────────────────────────────────────────────────────────

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _caretVisible = true;
        StartCaretTimer();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _caretVisible = false;
        StopCaretTimer();
        InvalidateVisual();
    }

}