using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Mnemo.Core.Models;
using Mnemo.Infrastructure.Services.LaTeX;
using Mnemo.UI.Controls;
using Mnemo.UI.Services;
using Mnemo.UI.Services.LaTeX.Layout.Boxes;
using Mnemo.UI.Services.LaTeX.Rendering;

namespace Mnemo.UI.Components.BlockEditor;

public partial class RichTextEditor
{
    // ── Inline equation rendering ────────────────────────────────────────────

    /// <summary>Raised when the inline equation flyout closes with a committed edit. Args: (charIndex, newLatex).</summary>
    public event Action<int, string>? InlineEquationEdited;

    internal void RaiseInlineEquationEdited(int charIndex, string latex)
        => InlineEquationEdited?.Invoke(charIndex, latex);

    internal async Task RebuildInlineEquationsAsyncCore()
    {
        var eq = _equations;
        if (eq == null) return;

        await eq.RebuildAsync(_lastLayoutWidth, Bounds.Width, MinLayoutWidth, FontSize).ConfigureAwait(true);

        var layoutWidth = _lastLayoutWidth > 0 ? _lastLayoutWidth : (Bounds.Width > 0 ? Bounds.Width : MinLayoutWidth);
        BuildLayout(layoutWidth);
        ComputeMathPadding();
        InvalidateVisual();

        if (eq.IsDirty)
            _ = RebuildInlineEquationsAsyncCore();
    }

    private static double ClampInlineEquationReserveWidth(double advance, string latex, double fontSize, double lineMaxWidth)
        => InlineEquationController.ClampReserveWidth(advance, latex, fontSize, lineMaxWidth);

    private static Rect CalculateMathPaintBounds(Box box, double x, double baselineY)
        => InlineEquationController.CalculateMathPaintBounds(box, x, baselineY);

    private void ComputeMathPadding()
    {
        if (_textLayout == null) { SetMathPadsIfChanged(0, 0, 0, 0); return; }
        var eq = _equations;
        if (eq == null) { SetMathPadsIfChanged(0, 0, 0, 0); return; }

        const double safety = 2;
        double padLeft = 0, padTop = 0, padRight = 0, padBottom = 0;
        var textH = _textLayout.Height;
        var textW = Math.Max(_textLayout.Width, _textLayout.WidthIncludingTrailingWhitespace);

        foreach (var entry in eq.Entries)
        {
            if (entry.Layout == null) continue;
            try
            {
                var layoutEq = LogicalCaretToLayoutBoundary(entry.CharIndex);
                var charRect = _textLayout.HitTestTextPosition(layoutEq);
                var x = charRect.X;
                var baselineY = GetTextBaselineYForLayoutIndex(_textLayout, layoutEq);
                var b = CalculateMathPaintBounds(entry.Layout, x, baselineY);
                padLeft = Math.Max(padLeft, Math.Max(0, safety - b.Left));
                padTop = Math.Max(padTop, Math.Max(0, safety - b.Top));
                padRight = Math.Max(padRight, Math.Max(0, b.Right + safety - textW));
                padBottom = Math.Max(padBottom, Math.Max(0, b.Bottom + safety - textH));
            }
            catch { }
        }

        SetMathPadsIfChanged(padLeft, padTop, padRight, padBottom);
    }

    private void SetMathPadsIfChanged(double l, double t, double r, double b)
    {
        if (Math.Abs(_mathPadLeft - l) < 0.25 && Math.Abs(_mathPadTop - t) < 0.25
            && Math.Abs(_mathPadRight - r) < 0.25 && Math.Abs(_mathPadBottom - b) < 0.25)
            return;
        _mathPadLeft = l;
        _mathPadTop = t;
        _mathPadRight = r;
        _mathPadBottom = b;
        InvalidateMeasure();
    }

    private double GetTextBaselineYForLayoutIndex(TextLayout layout, int layoutCharIndex)
    {
        var lines = layout.TextLines;
        if (lines.Count == 0) return 0;

        var trailing = layoutCharIndex >= _layoutTextLength;
        var idx = Math.Clamp(layoutCharIndex, 0, Math.Max(0, _layoutTextLength));
        var lineIndex = layout.GetLineIndexFromCharacterIndex(idx, trailing);
        lineIndex = Math.Clamp(lineIndex, 0, lines.Count - 1);

        double y = 0;
        for (var i = 0; i < lineIndex; i++)
            y += lines[i].Height;

        return y + lines[lineIndex].Baseline;
    }

    private void RenderInlineEquations(DrawingContext context)
    {
        var eq = _equations;
        if (eq == null || eq.Entries.Count == 0 || _textLayout == null) return;

        var foreground = Foreground ?? GetThemeForeground();
        var textBrush = foreground ?? Brushes.Black;
        var activeHl = GetEquationActiveHighlightBrush();

        foreach (var entry in eq.Entries)
        {
            if (entry.Layout == null) continue;
            try
            {
                var showSource = ShowInlineEquationSourceOnHover
                    && eq.HoveredCharIndex.HasValue
                    && eq.HoveredCharIndex.Value == entry.CharIndex;
                if (eq.PreviewActive && entry.CharIndex == eq.FlyoutCharIndex &&
                    TryGetInlineEquationBounds(entry, pad: 2, out var hl))
                {
                    context.DrawRectangle(activeHl, null, new RoundedRect(hl, 4, 4));
                }

                if (showSource)
                {
                    DrawInlineEquationSourceText(context, entry, textBrush);
                    continue;
                }

                var layoutEq = LogicalCaretToLayoutBoundary(entry.CharIndex);
                var charRect = _textLayout.HitTestTextPosition(layoutEq);
                var x = charRect.X;
                var baselineY = GetTextBaselineYForLayoutIndex(_textLayout, layoutEq);

                var renderContext = new MathRenderContext(context, textBrush, eq.MathTextCache);
                entry.Layout.Render(renderContext, x, baselineY);
            }
            catch { }
        }
    }

    private void DrawInlineEquationSourceText(DrawingContext context, InlineEquationEntry entry, IBrush foregroundBrush)
    {
        if (_textLayout == null) return;
        if (!TryGetInlineEquationBounds(entry, pad: 2, out var bounds)) return;

        using var latexLayout = new TextLayout(
            entry.Latex ?? string.Empty,
            new Typeface(MonoFont, FontStyle.Normal, FontWeight.Normal),
            Math.Max(11, FontSize * 0.88),
            foregroundBrush,
            TextAlignment.Left, TextWrapping.NoWrap, TextTrimming.CharacterEllipsis,
            null, FlowDirection.LeftToRight,
            Math.Max(bounds.Width - 4, 24));

        var drawPoint = new Point(bounds.X + 2, bounds.Y + Math.Max(0, (bounds.Height - latexLayout.Height) * 0.5));
        latexLayout.Draw(context, drawPoint);
    }
}
