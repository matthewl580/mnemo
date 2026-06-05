using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Mnemo.UI.Modules.Mindmap.Services;
using Mnemo.UI.Modules.Mindmap.ViewModels;

namespace Mnemo.UI.Modules.Mindmap.Views;

public partial class MindmapView
{
    private const int MinimapEaseMs = 250;

    private readonly MindmapCamera _camera = new();

    private Matrix TransformMatrix
    {
        get => _camera.Transform;
        set => _camera.SetTransform(value);
    }

    private static double GetScaleFromMatrix(Matrix matrix) => MindmapCamera.GetScaleFromMatrix(matrix);

    public void RecenterView()
    {
        if (DataContext is not MindmapViewModel vm || !vm.Nodes.Any()) return;

        var nodes = vm.Nodes;
        double centerX = (nodes.Min(n => n.X) + nodes.Max(n => n.X + n.ActualWidth)) / 2;
        double centerY = (nodes.Min(n => n.Y) + nodes.Max(n => n.Y + n.ActualHeight)) / 2;

        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null) return;

        double viewportWidth = mainCanvas.Bounds.Width;
        double viewportHeight = mainCanvas.Bounds.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        _camera.RecenterOnContentCenter(centerX, centerY, viewportWidth, viewportHeight);
        UpdateTransform();
    }

    private void OnCanvasPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var zoomDelta = e.Delta.Y > 0 ? 1.1 : 0.9;
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        var pointerPos = mainCanvas != null ? e.GetPosition(mainCanvas) : e.GetPosition(this);

        if (!_camera.TryZoomAt(pointerPos, zoomDelta))
            return;

        UpdateTransform();

        if (DataContext is MindmapViewModel vm)
            vm.ZoomLevel = _camera.Scale;

        if (_isSelecting)
        {
            _selectionCurrentInCanvas = _camera.ScreenToContent(pointerPos);
            UpdateSelectionBoxFromContentSpace();
        }

        e.Handled = true;
    }

    private void UpdateTransform()
    {
        var canvas = this.FindControl<Panel>("TransformCanvas");
        if (canvas != null)
        {
            canvas.RenderTransform = new MatrixTransform(TransformMatrix);
            canvas.InvalidateArrange();
            canvas.InvalidateVisual();
        }

        UpdateGrid();
        UpdateMinimap();
        UpdateMinimapVisibility();
        Dispatcher.UIThread.Post(UpdateMinimap, DispatcherPriority.Loaded);
    }

    private void ApplyZoomFromVm(double scale)
    {
        scale = Math.Clamp(scale, MindmapCamera.MinScale, MindmapCamera.MaxScale);
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null || mainCanvas.Bounds.Width <= 0 || mainCanvas.Bounds.Height <= 0)
        {
            double tx = TransformMatrix.M31;
            double ty = TransformMatrix.M32;
            TransformMatrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(tx, ty);
        }
        else
        {
            var viewportCenter = new Point(mainCanvas.Bounds.Width / 2, mainCanvas.Bounds.Height / 2);
            var contentPointAtCenter = _camera.ScreenToContent(viewportCenter);
            _camera.SetTransform(Matrix.CreateScale(scale, scale));
            _camera.CenterOnContentPoint(contentPointAtCenter, mainCanvas.Bounds.Width, mainCanvas.Bounds.Height);
        }

        UpdateTransform();
    }

    private void CenterViewportOnContentPoint(Point contentPoint)
    {
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null) return;
        double vpW = mainCanvas.Bounds.Width > 0 ? mainCanvas.Bounds.Width : _lastMainCanvasWidth;
        double vpH = mainCanvas.Bounds.Height > 0 ? mainCanvas.Bounds.Height : _lastMainCanvasHeight;
        if (vpW <= 0 || vpH <= 0) return;

        _camera.CenterOnContentPoint(contentPoint, vpW, vpH);
        UpdateTransform();
    }

    private void CenterViewportOnContentPointWithEase(Point contentPoint)
    {
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null) return;
        double vpW = mainCanvas.Bounds.Width > 0 ? mainCanvas.Bounds.Width : _lastMainCanvasWidth;
        double vpH = mainCanvas.Bounds.Height > 0 ? mainCanvas.Bounds.Height : _lastMainCanvasHeight;
        if (vpW <= 0 || vpH <= 0) return;

        double scale = _camera.Scale;
        double targetOffsetX = vpW / 2.0 - contentPoint.X * scale;
        double targetOffsetY = vpH / 2.0 - contentPoint.Y * scale;
        var targetMatrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(targetOffsetX, targetOffsetY);

        _easeTimer?.Stop();
        _easeTimer = null;

        var startMatrix = TransformMatrix;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _easeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        void Tick(object? s, EventArgs args)
        {
            double elapsed = sw.ElapsedMilliseconds / (double)MinimapEaseMs;
            if (elapsed >= 1)
            {
                TransformMatrix = targetMatrix;
                UpdateTransform();
                _easeTimer?.Stop();
                _easeTimer = null;
                return;
            }

            double invElapsed = 1 - elapsed;
            double eased = 1 - (invElapsed * invElapsed * invElapsed);
            double m31 = startMatrix.M31 + (targetMatrix.M31 - startMatrix.M31) * eased;
            double m32 = startMatrix.M32 + (targetMatrix.M32 - startMatrix.M32) * eased;
            TransformMatrix = new Matrix(scale, 0, 0, scale, m31, m32);
            UpdateTransform();
        }

        _easeTimer.Tick += Tick;
        _easeTimer.Start();
    }
}
