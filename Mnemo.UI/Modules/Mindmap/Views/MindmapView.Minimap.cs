using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Mnemo.UI.Modules.Mindmap.ViewModels;

namespace Mnemo.UI.Modules.Mindmap.Views;

public partial class MindmapView
{
    private Matrix _minimapMatrix = Matrix.Identity;
    private Rect _viewportRectInMinimap;

    private enum MinimapDragMode { None, ViewportRect, Outside }

    private MinimapDragMode _minimapDragMode = MinimapDragMode.None;
    private Point _minimapPressContentPoint;
    private Point _minimapLastMmPoint;
    private bool _minimapHasMoved;
    private double _lastMinimapWidth;
    private double _lastMinimapHeight;

    private const double MinimapZoomThreshold = 0.6;

    internal void UpdateMinimapVisibility()
    {
        var minimapBorder = this.FindControl<Border>("MinimapBorder");
        if (minimapBorder == null) return;

        string mode = DataContext is MindmapViewModel vm ? vm.MinimapVisibilityMode : "Auto";
        bool byZoom = GetScaleFromMatrix(TransformMatrix) <= MinimapZoomThreshold;
        bool visible = mode switch
        {
            "Off" => false,
            "On" => true,
            _ => byZoom
        };

        minimapBorder.Opacity = visible ? 1 : 0;
        minimapBorder.IsHitTestVisible = visible;
    }

    internal void UpdateMinimap()
    {
        var minimapPanel = this.FindControl<Panel>("MinimapPanel");
        var minimapTransformPanel = this.FindControl<Panel>("MinimapTransformPanel");
        var minimapViewportBox = this.FindControl<Border>("MinimapViewportBox");
        var mainCanvas = this.FindControl<Panel>("MainCanvas");

        if (minimapPanel == null || minimapTransformPanel == null || minimapViewportBox == null || mainCanvas == null)
            return;

        if (DataContext is not MindmapViewModel vm || !vm.Nodes.Any())
        {
            minimapViewportBox.IsVisible = false;
            return;
        }

        minimapViewportBox.IsVisible = true;

        double minimapWidth = minimapPanel.Bounds.Width > 0 ? minimapPanel.Bounds.Width : _lastMinimapWidth;
        double minimapHeight = minimapPanel.Bounds.Height > 0 ? minimapPanel.Bounds.Height : _lastMinimapHeight;
        if (minimapWidth <= 0 || minimapHeight <= 0) return;

        _lastMinimapWidth = minimapWidth;
        _lastMinimapHeight = minimapHeight;

        var nodes = vm.ShowCollapsedNodesOnMinimap
            ? vm.Nodes.ToList()
            : vm.Nodes.Where(n => !n.IsHidden).ToList();
        if (nodes.Count == 0) nodes = vm.Nodes.ToList();

        double minX = nodes.Min(n => n.X);
        double maxX = nodes.Max(n => n.X + n.ActualWidth);
        double minY = nodes.Min(n => n.Y);
        double maxY = nodes.Max(n => n.Y + n.ActualHeight);
        double contentWidth = maxX - minX;
        double contentHeight = maxY - minY;

        const double padding = 10;
        double scaleX = (minimapWidth - padding * 2) / Math.Max(contentWidth, 1);
        double scaleY = (minimapHeight - padding * 2) / Math.Max(contentHeight, 1);
        double mmScaleFit = Math.Min(scaleX, scaleY);
        double viewportScale = GetScaleFromMatrix(TransformMatrix);
        double mmScale = mmScaleFit * viewportScale;

        double mmOffsetX = minimapWidth / 2.0 - (minX + contentWidth / 2.0) * mmScale;
        double mmOffsetY = minimapHeight / 2.0 - (minY + contentHeight / 2.0) * mmScale;

        var minimapMatrix = Matrix.CreateScale(mmScale, mmScale)
                          * Matrix.CreateTranslation(mmOffsetX, mmOffsetY);

        if (minimapTransformPanel.RenderTransform is MatrixTransform mmt)
            mmt.Matrix = minimapMatrix;
        else
            minimapTransformPanel.RenderTransform = new MatrixTransform(minimapMatrix);

        _minimapMatrix = minimapMatrix;

        minimapTransformPanel.Width = minimapWidth;
        minimapTransformPanel.Height = minimapHeight;
        minimapTransformPanel.InvalidateVisual();

        double vpWidth = mainCanvas.Bounds.Width > 0 ? mainCanvas.Bounds.Width : _lastMainCanvasWidth;
        double vpHeight = mainCanvas.Bounds.Height > 0 ? mainCanvas.Bounds.Height : _lastMainCanvasHeight;
        if (vpWidth <= 0 || vpHeight <= 0) return;

        _lastMainCanvasWidth = vpWidth;
        _lastMainCanvasHeight = vpHeight;

        double det = TransformMatrix.M11 * TransformMatrix.M22 - TransformMatrix.M12 * TransformMatrix.M21;
        if (Math.Abs(det) < 1e-10) return;

        var invMain = TransformMatrix.Invert();
        var tlContent = new Point(0, 0) * invMain;
        var brContent = new Point(vpWidth, vpHeight) * invMain;
        var tlMm = tlContent * minimapMatrix;
        var brMm = brContent * minimapMatrix;

        double boxX = Math.Min(tlMm.X, brMm.X);
        double boxY = Math.Min(tlMm.Y, brMm.Y);
        double boxW = Math.Abs(brMm.X - tlMm.X);
        double boxH = Math.Abs(brMm.Y - tlMm.Y);

        _viewportRectInMinimap = new Rect(boxX, boxY, boxW, boxH);

        const double viewportBoxScale = 1;
        double displayW = Math.Max(boxW * viewportBoxScale, 2);
        double displayH = Math.Max(boxH * viewportBoxScale, 2);
        double displayX = boxX + (boxW - displayW) / 2;
        double displayY = boxY + (boxH - displayH) / 2;

        minimapViewportBox.Width = displayW;
        minimapViewportBox.Height = displayH;

        if (minimapViewportBox.RenderTransform is TranslateTransform tt)
        {
            tt.X = displayX;
            tt.Y = displayY;
        }
        else
            minimapViewportBox.RenderTransform = new TranslateTransform(displayX, displayY);

        minimapViewportBox.InvalidateVisual();
    }

    private void OnMinimapSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _lastMinimapWidth = e.NewSize.Width;
            _lastMinimapHeight = e.NewSize.Height;
        }

        UpdateMinimap();
    }

    private void OnMinimapPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed) return;

        var minimapPanel = this.FindControl<Panel>("MinimapPanel");
        if (minimapPanel == null) return;

        var ptMm = e.GetPosition(minimapPanel);
        _minimapHasMoved = false;
        bool onViewport = _viewportRectInMinimap.Contains(ptMm);

        if (onViewport)
        {
            _minimapDragMode = MinimapDragMode.ViewportRect;
            _minimapLastMmPoint = ptMm;
        }
        else
        {
            _minimapDragMode = MinimapDragMode.Outside;
            _minimapLastMmPoint = ptMm;
            var invMinimap = _minimapMatrix.Invert();
            _minimapPressContentPoint = ptMm * invMinimap;
            CenterViewportOnContentPoint(_minimapPressContentPoint);
        }

        e.Handled = true;
    }

    private void OnMinimapPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_minimapDragMode == MinimapDragMode.None) return;

        var minimapPanel = this.FindControl<Panel>("MinimapPanel");
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (minimapPanel == null || mainCanvas == null) return;

        var ptMm = e.GetPosition(minimapPanel);
        double mmScale = Math.Sqrt(_minimapMatrix.M11 * _minimapMatrix.M11 + _minimapMatrix.M12 * _minimapMatrix.M12);
        if (mmScale < 1e-10) return;

        if (Math.Abs(ptMm.X - _minimapLastMmPoint.X) > ClickDragThreshold ||
            Math.Abs(ptMm.Y - _minimapLastMmPoint.Y) > ClickDragThreshold)
            _minimapHasMoved = true;

        if (_minimapDragMode == MinimapDragMode.ViewportRect)
        {
            double scale = GetScaleFromMatrix(TransformMatrix);
            double contentDx = (ptMm.X - _minimapLastMmPoint.X) / mmScale;
            double contentDy = (ptMm.Y - _minimapLastMmPoint.Y) / mmScale;
            _camera.PanByScreenDelta(contentDx * scale, contentDy * scale);
            UpdateTransform();
        }
        else if (_minimapDragMode == MinimapDragMode.Outside)
        {
            var invMinimap = _minimapMatrix.Invert();
            var contentPoint = ptMm * invMinimap;
            CenterViewportOnContentPoint(contentPoint);
        }

        _minimapLastMmPoint = ptMm;
        e.Handled = true;
    }

    private void OnMinimapPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_minimapDragMode == MinimapDragMode.Outside && !_minimapHasMoved)
            CenterViewportOnContentPointWithEase(_minimapPressContentPoint);

        _minimapDragMode = MinimapDragMode.None;
        e.Handled = true;
    }
}
