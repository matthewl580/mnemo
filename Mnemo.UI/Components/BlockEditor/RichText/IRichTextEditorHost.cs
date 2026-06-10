using Avalonia;
using Avalonia.Media.TextFormatting;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Narrow callback surface for <see cref="RichTextEditor"/> collaborators that need
/// layout invalidation and geometry without depending on the full control API.
/// </summary>
internal interface IRichTextEditorHost
{
    void InvalidateEditorVisual();
    void InvalidateEditorMeasure();
    Rect EditorBounds { get; }
    TextLayout? ForegroundTextLayout { get; }
    int TextLength { get; }
    int LogicalToLayoutBoundary(int logicalIndex);
}
