using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Renders find-in-note search highlight rectangles over a <see cref="TextLayout"/>.
/// Pure geometry; no control state.
/// </summary>
internal static class RichTextSearchHighlightRenderer
{
    internal static void Render(
        DrawingContext context,
        TextLayout? textLayout,
        int textLength,
        IReadOnlyList<RichTextEditor.SearchHighlightRange>? ranges,
        RichTextEditor.SearchHighlightRange? activeRange,
        Func<int, int> logicalToLayoutBoundary)
    {
        if (textLayout == null || ranges == null || ranges.Count == 0)
            return;

        var activeBrush = RichTextThemeBrushes.GetActiveSearchHighlightBrush();
        var normalBrush = RichTextThemeBrushes.GetSearchHighlightBrush();

        foreach (var range in ranges)
        {
            if (range.Length <= 0)
                continue;

            int logicalStart = Math.Clamp(range.Start, 0, textLength);
            int logicalEnd = Math.Clamp(range.Start + range.Length, 0, textLength);
            if (logicalEnd <= logicalStart)
                continue;

            int layoutStart = logicalToLayoutBoundary(logicalStart);
            int layoutLen = logicalToLayoutBoundary(logicalEnd) - layoutStart;
            if (layoutLen <= 0)
                continue;

            var rects = textLayout.HitTestTextRange(layoutStart, layoutLen).ToList();
            var brush = activeRange.HasValue
                && activeRange.Value.Start == range.Start
                && activeRange.Value.Length == range.Length
                ? activeBrush
                : normalBrush;

            foreach (var rect in rects)
            {
                if (rect.Width <= 0.5 || rect.Height <= 0.5)
                    continue;
                context.FillRectangle(brush, rect);
            }
        }
    }
}
