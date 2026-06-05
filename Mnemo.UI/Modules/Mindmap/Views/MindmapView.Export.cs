using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Collections;
using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.ViewModels;

namespace Mnemo.UI.Modules.Mindmap.Views;

public partial class MindmapView
{
    private const double ExportScale = 2.0;
    private const double ExportNodeHorizontalPaddingTotal = 24;
    private const double ExportSelectionPadding = 24;
    private const double ExportNodeWidthPadding = 24;
    private const double ExportNodeHeightPadding = 12;
    private const double ExportStrokeThickness = 1.5;
    private const double ExportFontSize = 14;

    private async void OnExportRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MindmapViewModel vm)
            return;

        try
        {
            if (vm.Nodes.Count == 0) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            var selectedIds = vm.HasSelectedNodes
                ? vm.Nodes.Where(n => n.IsSelected).Select(n => n.Id).ToHashSet()
                : vm.Nodes.Select(n => n.Id).ToHashSet();

            string suggestedName = (vm.Title ?? "mindmap").Trim();
            if (string.IsNullOrEmpty(suggestedName)) suggestedName = "mindmap";
            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                suggestedName = suggestedName.Replace(c, '_');

            var dialogTitle = vm.Translate("ExportAsPngDialogTitle", "Mindmap", "Export as PNG");

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = dialogTitle,
                SuggestedFileName = suggestedName + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[] { new FilePickerFileType("PNG") { Patterns = new[] { "*.png" } } }
            }).ConfigureAwait(true);

            if (file == null) return;

            var selectedForCapture = vm.HasSelectedNodes ? selectedIds : null;
            var pngBytes = CaptureCurrentViewport(vm.ExportPngTransparentBackground, selectedForCapture);
            if (pngBytes == null || pngBytes.Length == 0)
            {
                var msg = vm.Translate("ExportFailedNothingCaptured", "Mindmap", "Nothing was captured.");
                await vm.ShowExportErrorAsync(msg).ConfigureAwait(true);
                return;
            }

            await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
            await stream.WriteAsync(pngBytes).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            vm.LogExportWarning(ex);
            await vm.ShowExportErrorAsync(ex.Message).ConfigureAwait(true);
        }
    }

    private IBrush GetExportBrush(string? hex, string fallbackResourceKey)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return this.FindResource(fallbackResourceKey) as IBrush ?? Brushes.Gray;
    }

    private static AvaloniaList<double>? GetExportStrokeDashArray(string? type) =>
        type switch
        {
            EdgeTypes.Dashed => new AvaloniaList<double> { 6, 4 },
            EdgeTypes.Dotted => new AvaloniaList<double> { 2, 3 },
            _ => null
        };

    private static double GetExportNodeInnerTextMaxWidth(NodeViewModel node)
    {
        double outer = node.Width ?? NodeViewModel.DefaultMaxOuterWidth;
        double collapseReserve = node.HasChildren ? 28.0 : 0.0;
        return Math.Max(40, outer - ExportNodeHorizontalPaddingTotal - collapseReserve);
    }

    private static (double Width, double Height) MeasureNodeText(string? text, double maxTextWidth)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        var ft = new FormattedText(
            text.Replace("\r\n", "\n"),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium),
            ExportFontSize,
            Brushes.Black);
        ft.TextAlignment = TextAlignment.Center;
        ft.MaxTextWidth = maxTextWidth;
        return (ft.Width, ft.Height);
    }

    private byte[]? CaptureCurrentViewport(bool transparentBackground, IReadOnlySet<string>? selectedNodeIds = null)
    {
        if (DataContext is not MindmapViewModel vm) return null;
        var contentOnly = this.FindControl<Panel>("MindmapContentOnly");
        if (contentOnly == null) return null;

        double viewportW = contentOnly.Bounds.Width > 0 ? contentOnly.Bounds.Width : _lastMainCanvasWidth;
        double viewportH = contentOnly.Bounds.Height > 0 ? contentOnly.Bounds.Height : _lastMainCanvasHeight;
        if (viewportW <= 0 || viewportH <= 0) return null;

        var matrix = TransformMatrix;
        var nodesInScope = selectedNodeIds != null
            ? vm.Nodes.Where(n => selectedNodeIds.Contains(n.Id)).ToList()
            : vm.Nodes.ToList();
        var edgesInScope = selectedNodeIds != null
            ? vm.Edges.Where(e => selectedNodeIds.Contains(e.From.Id) && selectedNodeIds.Contains(e.To.Id)).ToList()
            : vm.Edges.ToList();

        double w, h, offsetX, offsetY;

        if (selectedNodeIds != null && nodesInScope.Count > 0)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var node in nodesInScope)
            {
                double innerW = GetExportNodeInnerTextMaxWidth(node);
                var (mw, mh) = MeasureNodeText(node.Text, innerW);
                double collapseW = node.HasChildren ? 28.0 : 0.0;
                double nw = node.Width ?? Math.Max(NodeViewModel.DefaultWidth, mw + ExportNodeWidthPadding + collapseW);
                double nh = node.Height ?? Math.Max(NodeViewModel.DefaultHeight, mh + ExportNodeHeightPadding);
                var tl = matrix.Transform(new Point(node.X, node.Y));
                var br = matrix.Transform(new Point(node.X + nw, node.Y + nh));
                minX = Math.Min(minX, tl.X);
                minY = Math.Min(minY, tl.Y);
                maxX = Math.Max(maxX, br.X);
                maxY = Math.Max(maxY, br.Y);
            }

            foreach (var edge in edgesInScope)
            {
                var p1 = matrix.Transform(edge.StartPoint);
                var p4 = matrix.Transform(edge.EndPoint);
                minX = Math.Min(minX, Math.Min(p1.X, p4.X));
                minY = Math.Min(minY, Math.Min(p1.Y, p4.Y));
                maxX = Math.Max(maxX, Math.Max(p1.X, p4.X));
                maxY = Math.Max(maxY, Math.Max(p1.Y, p4.Y));
            }

            offsetX = -minX + ExportSelectionPadding;
            offsetY = -minY + ExportSelectionPadding;
            w = maxX - minX + 2 * ExportSelectionPadding;
            h = maxY - minY + 2 * ExportSelectionPadding;
            if (w <= 0 || h <= 0) return null;
        }
        else
        {
            offsetX = 0;
            offsetY = 0;
            w = viewportW;
            h = viewportH;
        }

        var bgBrush = transparentBackground
            ? Brushes.Transparent
            : (this.FindResource("WorkspaceBackgroundBrush") as IBrush ?? Brushes.White);
        var nodeBgBrush = this.FindResource("CardBackgroundSecondaryBrush") as IBrush ?? Brushes.White;
        var textBrush = this.FindResource("TextPrimaryBrush") as IBrush ?? Brushes.Black;

        var exportPanel = new Canvas
        {
            Width = w,
            Height = h,
            Background = bgBrush,
            IsHitTestVisible = false
        };

        Point ToPanel(Point screen) => new(screen.X + offsetX, screen.Y + offsetY);

        foreach (var edge in edgesInScope)
        {
            var strokeBrush = GetExportBrush(edge.Color, "MindmapEdgeStrokeBrush");
            var dashArray = GetExportStrokeDashArray(edge.Type);

            var p1 = ToPanel(matrix.Transform(edge.StartPoint));
            var p2 = ToPanel(matrix.Transform(edge.ControlPoint1));
            var p3 = ToPanel(matrix.Transform(edge.ControlPoint2));
            var p4 = ToPanel(matrix.Transform(edge.EndPoint));
            var figure = new PathFigure { StartPoint = p1, IsClosed = false };
            figure.Segments!.Add(new BezierSegment { Point1 = p2, Point2 = p3, Point3 = p4 });
            var pathGeometry = new PathGeometry();
            pathGeometry.Figures!.Add(figure);
            exportPanel.Children.Add(new Avalonia.Controls.Shapes.Path
            {
                Data = pathGeometry,
                Stroke = strokeBrush,
                StrokeThickness = ExportStrokeThickness,
                StrokeDashArray = dashArray,
                IsHitTestVisible = false
            });

            if (edge.Type == EdgeTypes.Double)
            {
                var o1 = ToPanel(matrix.Transform(edge.OffsetStartPoint));
                var o2 = ToPanel(matrix.Transform(edge.OffsetControlPoint1));
                var o3 = ToPanel(matrix.Transform(edge.OffsetControlPoint2));
                var o4 = ToPanel(matrix.Transform(edge.OffsetEndPoint));
                var ofig = new PathFigure { StartPoint = o1, IsClosed = false };
                ofig.Segments!.Add(new BezierSegment { Point1 = o2, Point2 = o3, Point3 = o4 });
                var ogeom = new PathGeometry();
                ogeom.Figures!.Add(ofig);
                exportPanel.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Data = ogeom,
                    Stroke = strokeBrush,
                    StrokeThickness = ExportStrokeThickness,
                    StrokeDashArray = dashArray,
                    IsHitTestVisible = false
                });
            }

            if (edge.Type == EdgeTypes.Bidirect && edge.ArrowStartPoints.Count >= 3)
            {
                var startPts = edge.ArrowStartPoints.Select(p => ToPanel(matrix.Transform(p))).ToList();
                exportPanel.Children.Add(new Polygon
                {
                    Points = new Points(startPts),
                    Fill = strokeBrush,
                    IsHitTestVisible = false
                });
            }

            if ((edge.Type == EdgeTypes.Arrow || edge.Type == EdgeTypes.Bidirect) && edge.ArrowEndPoints.Count >= 3)
            {
                var endPts = edge.ArrowEndPoints.Select(p => ToPanel(matrix.Transform(p))).ToList();
                exportPanel.Children.Add(new Polygon
                {
                    Points = new Points(endPts),
                    Fill = strokeBrush,
                    IsHitTestVisible = false
                });
            }
        }

        foreach (var node in nodesInScope)
        {
            double innerW = GetExportNodeInnerTextMaxWidth(node);
            var (mw, mh) = MeasureNodeText(node.Text, innerW);
            double collapseW = node.HasChildren ? 28.0 : 0.0;
            double nw = node.Width ?? Math.Max(NodeViewModel.DefaultWidth, mw + ExportNodeWidthPadding + collapseW);
            double nh = node.Height ?? Math.Max(NodeViewModel.DefaultHeight, mh + ExportNodeHeightPadding);
            var topLeft = ToPanel(matrix.Transform(new Point(node.X, node.Y)));
            var bottomRight = ToPanel(matrix.Transform(new Point(node.X + nw, node.Y + nh)));
            double screenW = Math.Max(1, bottomRight.X - topLeft.X);
            double screenH = Math.Max(1, bottomRight.Y - topLeft.Y);
            var borderBrush = GetExportBrush(node.Color, "MindmapToolbarNodeColorSwatchOneBrush");
            var radius = node.CornerRadius;
            exportPanel.Children.Add(new Border
            {
                [Canvas.LeftProperty] = topLeft.X,
                [Canvas.TopProperty] = topLeft.Y,
                Width = screenW,
                Height = screenH,
                Background = nodeBgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(ExportStrokeThickness),
                CornerRadius = new CornerRadius(radius > 100 ? screenH / 2 : Math.Min(radius, screenH / 2)),
                Padding = new Thickness(12, 6),
                Child = new TextBlock
                {
                    Text = (node.Text ?? "").Replace("\r\n", "\n"),
                    Foreground = textBrush,
                    FontSize = ExportFontSize,
                    FontWeight = FontWeight.Medium,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = Math.Max(40, nw - ExportNodeHorizontalPaddingTotal - collapseW)
                },
                IsHitTestVisible = false
            });
        }

        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null) return null;

        try
        {
            mainCanvas.Children.Add(exportPanel);
            Canvas.SetLeft(exportPanel, -w - 100);
            Canvas.SetTop(exportPanel, -h - 100);
            exportPanel.Measure(new Size(w, h));
            exportPanel.Arrange(new Rect(0, 0, w, h));

            int pw = (int)Math.Ceiling(w * ExportScale);
            int ph = (int)Math.Ceiling(h * ExportScale);
            var dpi = new Vector(96 * ExportScale, 96 * ExportScale);
            using var bmp = new RenderTargetBitmap(new PixelSize(pw, ph), dpi);
            exportPanel.IsVisible = true;
            bmp.Render(exportPanel);

            using var mem = new MemoryStream();
            bmp.Save(mem);
            return mem.ToArray();
        }
        finally
        {
            mainCanvas.Children.Remove(exportPanel);
        }
    }
}
