using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mnemo.UI.Components.BlockEditor.BlockComponents;
using Mnemo.UI.Components.BlockEditor.BlockComponents.Image;
using System;
using System.Threading.Tasks;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Manages gutter chrome visibility, drag-handle state, and the nested-column layout flag
/// for a single <see cref="EditableBlock"/>.
/// Owns <c>_blockGutterChromeVisible</c>, <c>_dragHandleReorderPressPoint</c>, and <c>_isNestedInColumn</c>.
/// </summary>
internal sealed class BlockChromeController
{
    private readonly EditableBlock _host;

    internal bool GutterVisible { get; private set; }
    internal bool IsNestedInColumn { get; private set; }

    private Point? _dragPressPoint;
    private PointerPressedEventArgs? _dragPressArgs;
    private bool _dragLaunched;

    private double _gutterMarginTopCache = double.NaN;

    private const double DragThresholdPixels = 6;
    private const double GutterChromeHeight = 26;

    internal BlockChromeController(EditableBlock host)
    {
        _host = host;
    }

    // ── Nested column layout ─────────────────────────────────────────────────

    internal void SetNestedInColumn(bool value)
    {
        if (IsNestedInColumn == value) return;
        IsNestedInColumn = value;
        ApplyNestedColumnLayout();
    }

    internal void ApplyNestedColumnLayout()
    {
        var grid = _host.ChromeGrid;
        if (grid == null) return;
        if (IsNestedInColumn)
        {
            grid.ColumnDefinitions[0].Width = new GridLength(0);
            grid.ColumnDefinitions[1].Width = new GridLength(0);
            if (_host.ChromeAddBelow != null) _host.ChromeAddBelow.IsVisible = false;
            if (_host.ChromeDragHandle != null) _host.ChromeDragHandle.IsVisible = false;
        }
        else
        {
            grid.ColumnDefinitions[0].Width = GridLength.Auto;
            grid.ColumnDefinitions[1].Width = GridLength.Auto;
            if (_host.ChromeAddBelow != null) _host.ChromeAddBelow.IsVisible = true;
            if (_host.ChromeDragHandle != null) _host.ChromeDragHandle.IsVisible = true;
        }
    }

    internal void ApplyReadOnlyState(bool readOnly)
    {
        if (_host.ChromeAddBelow != null)
            _host.ChromeAddBelow.IsVisible = !readOnly && !IsNestedInColumn;
        if (_host.ChromeDragHandle != null)
            _host.ChromeDragHandle.IsVisible = !readOnly && !IsNestedInColumn;
    }

    // ── Gutter chrome ────────────────────────────────────────────────────────

    internal void SetGutterVisible(bool visible)
    {
        GutterVisible = visible;

        if (_host.ChromeAddBelowIcon != null)
            _host.ChromeAddBelowIcon.Opacity = visible ? 1 : 0;
        if (_host.ChromeDragHandleGrip != null)
            _host.ChromeDragHandleGrip.Opacity = visible ? 0.4 : 0;

        if (!visible)
        {
            ClearGutterHoverBackground(_host.ChromeAddBelow);
            ClearGutterHoverBackground(_host.ChromeDragHandle);
        }

        InvalidateGutterChrome();
    }

    internal void InvalidateGutterChrome()
    {
        _host.ChromeAddBelow?.InvalidateVisual();
        _host.ChromeDragHandle?.InvalidateVisual();
        _host.ChromeAddBelowIcon?.InvalidateVisual();
        _host.ChromeDragHandleGrip?.InvalidateVisual();
        _host.InvalidateVisual();
    }

    internal void OnGutterBorderPointerEntered(Border? border)
    {
        if (!GutterVisible || border == null) return;
        if (Avalonia.Application.Current?.TryFindResource("ListItemHoverBackgroundBrush", out var res) == true && res is IBrush brush)
            border.Background = brush;
    }

    internal void OnGutterBorderPointerExited(Border? border) => ClearGutterHoverBackground(border);

    private static void ClearGutterHoverBackground(Border? border)
    {
        if (border != null) border.Background = Brushes.Transparent;
    }

    // ── Gutter vertical alignment ────────────────────────────────────────────

    internal void UpdateGutterVerticalAlignment()
    {
        var addBelow = _host.ChromeAddBelow;
        var dragHandle = _host.ChromeDragHandle;
        var grid = _host.ChromeGrid;

        if (addBelow == null || dragHandle == null || grid == null) return;

        double offsetY = 0;

        if (_host._currentBlockComponent is ImageBlockComponent { GutterAnchorRow: { } imageRow })
        {
            var p = imageRow.TranslatePoint(new Point(0, 0), grid);
            if (p.HasValue && imageRow.Bounds.Height > 0)
                offsetY = Math.Max(0, p.Value.Y + (imageRow.Bounds.Height - GutterChromeHeight) * 0.5);
        }
        else if (_host._currentBlockComponent?.GetRichTextEditor() is RichTextEditor rte)
        {
            var line = rte.GetFirstLineBounds();
            var p = rte.TranslatePoint(new Point(0, line.Y), grid);
            if (p.HasValue && line.Height > 0)
                offsetY = Math.Max(0, p.Value.Y + (line.Height - GutterChromeHeight) * 0.5);
        }
        else if (_host._currentBlockComponent?.GetLegacyTextBox() is TextBox tb)
        {
            var p = tb.TranslatePoint(new Point(0, 0), grid);
            if (p.HasValue)
            {
                var lineH = tb.FontSize * 1.25;
                offsetY = Math.Max(0, p.Value.Y + (lineH - GutterChromeHeight) * 0.5);
            }
        }
        else if (_host._currentBlockComponent != null)
        {
            var c = _host._currentBlockComponent;
            var p = c.TranslatePoint(new Point(0, 0), grid);
            if (p.HasValue && c.Bounds.Height > 0)
                offsetY = Math.Max(0, p.Value.Y + (c.Bounds.Height - GutterChromeHeight) * 0.5);
        }

        if (!double.IsNaN(_gutterMarginTopCache) && Math.Abs(offsetY - _gutterMarginTopCache) < 0.25)
            return;
        _gutterMarginTopCache = offsetY;

        var tx = new TranslateTransform(0, offsetY);
        addBelow.RenderTransform = tx;
        dragHandle.RenderTransform = tx;
    }

    internal void ResetGutterMarginCache() => _gutterMarginTopCache = double.NaN;

    // ── Drag handle ──────────────────────────────────────────────────────────

    internal void OnDragHandlePointerPressed(PointerPressedEventArgs e)
    {
        var dragHandle = _host.ChromeDragHandle;
        if (!GutterVisible || _host._viewModel == null || dragHandle == null) return;
        if (!e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed) return;

        _dragLaunched = false;
        _dragPressPoint = e.GetPosition(dragHandle);
        _dragPressArgs = e;
        e.Pointer.Capture(dragHandle);
        e.Handled = true;
    }

    internal void OnDragHandlePointerMoved(PointerEventArgs e)
    {
        var dragHandle = _host.ChromeDragHandle;
        if (dragHandle == null || !_dragPressPoint.HasValue || _dragLaunched) return;
        if (!ReferenceEquals(e.Pointer.Captured, dragHandle)) return;
        if (!e.GetCurrentPoint(dragHandle).Properties.IsLeftButtonPressed) return;

        var p = e.GetPosition(dragHandle);
        var origin = _dragPressPoint.Value;
        var dx = p.X - origin.X;
        var dy = p.Y - origin.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= DragThresholdPixels)
        {
            _dragLaunched = true;
            _ = RunDragReorderAsync();
        }
    }

    internal void OnDragHandlePointerReleased(PointerReleasedEventArgs e)
    {
        var dragHandle = _host.ChromeDragHandle;
        if (dragHandle == null) return;
        if (e.GetCurrentPoint(dragHandle).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased) return;

        if (!_dragLaunched && _dragPressPoint.HasValue && _host._viewModel != null)
            _host.FindParentBlockEditor()?.SelectBlockFromDragHandle(_host._viewModel, e.KeyModifiers);

        if (ReferenceEquals(e.Pointer.Captured, dragHandle))
            e.Pointer.Capture(null);

        ClearDragGestureState();
    }

    internal void OnDragHandlePointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        if (_dragLaunched) return;
        ClearDragGestureState();
    }

    private void ClearDragGestureState()
    {
        _dragPressPoint = null;
        _dragPressArgs = null;
        _dragLaunched = false;
    }

    private async Task RunDragReorderAsync()
    {
        try
        {
            if (_dragPressArgs == null) return;
            var dragHandle = _host.ChromeDragHandle;
            if (dragHandle != null && ReferenceEquals(_dragPressArgs.Pointer.Captured, dragHandle))
                _dragPressArgs.Pointer.Capture(null);
            await _host.BeginBlockReorderDragCoreAsync(_dragPressArgs).ConfigureAwait(true);
        }
        finally
        {
            ClearDragGestureState();
        }
    }
}
