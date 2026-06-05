using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Mnemo.UI.Modules.Mindmap.Services;
using Mnemo.UI.Modules.Mindmap.ViewModels;

namespace Mnemo.UI.Modules.Mindmap.Views;

public partial class MindmapView
{
    private const string MindmapGridColorKey = "MindmapGridColor";
    private const double DotScaleExponent = 0.35;
    private const double MinDotSourceSize = 0.5;
    private const double MaxDotSourceSize = 10.0;

    private VisualBrush? _gridBrush;

    private MindmapCanvasSettings? GridSettings =>
        DataContext is MindmapViewModel vm ? vm.CanvasSettings : null;

    private void OnCanvasSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MindmapCanvasSettings.GridType)
            or nameof(MindmapCanvasSettings.GridSpacing)
            or nameof(MindmapCanvasSettings.GridDotSize)
            or nameof(MindmapCanvasSettings.GridOpacity))
        {
            _gridBrush = null;
            UpdateGrid();
        }
    }

    private Color? GetGridColorFromTheme()
    {
        return this.TryFindResource(MindmapGridColorKey, out var value) && value is Color color ? color : null;
    }

    private VisualBrush CreateGridBrush(Color gridColor, MindmapCanvasSettings settings)
    {
        if (settings.GridType == "None") return new VisualBrush();

        var stroke = new SolidColorBrush(gridColor) { Opacity = settings.GridOpacity };
        var gridCanvas = new Canvas
        {
            Width = settings.GridSpacing,
            Height = settings.GridSpacing,
            Background = Brushes.Transparent
        };

        if (settings.GridType == "Dotted")
        {
            gridCanvas.Children.Add(new Ellipse
            {
                Width = settings.GridDotSize,
                Height = settings.GridDotSize,
                Fill = stroke,
                [Canvas.LeftProperty] = 0,
                [Canvas.TopProperty] = 0
            });
        }
        else if (settings.GridType == "Lines")
        {
            gridCanvas.Children.Add(new Line
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(settings.GridSpacing, 0),
                Stroke = stroke,
                StrokeThickness = settings.GridDotSize
            });
            gridCanvas.Children.Add(new Line
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, settings.GridSpacing),
                Stroke = stroke,
                StrokeThickness = settings.GridDotSize
            });
        }

        return new VisualBrush
        {
            Visual = gridCanvas,
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(0, 0, settings.GridSpacing, settings.GridSpacing, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(0, 0, settings.GridSpacing, settings.GridSpacing, RelativeUnit.Absolute)
        };
    }

    internal void UpdateGrid()
    {
        var gridCanvas = this.FindControl<Canvas>("GridCanvas");
        var settings = GridSettings;
        if (gridCanvas == null || settings == null) return;

        if (settings.GridType == "None")
        {
            gridCanvas.Background = null;
            return;
        }

        var gridColor = GetGridColorFromTheme();
        if (gridColor == null)
        {
            gridCanvas.Background = null;
            return;
        }

        if (_gridBrush == null)
            _gridBrush = CreateGridBrush(gridColor.Value, settings);

        double scale = GetScaleFromMatrix(TransformMatrix);

        if (settings.GridType == "Dotted")
        {
            double dotScreenSize = settings.GridDotSize * scale * Math.Pow(scale, DotScaleExponent - 1);
            double dotSizeSource = Math.Clamp(dotScreenSize / scale, MinDotSourceSize, MaxDotSourceSize);

            if (_gridBrush.Visual is Canvas visualCanvas && visualCanvas.Children.FirstOrDefault() is Ellipse ellipse)
            {
                ellipse.Width = dotSizeSource;
                ellipse.Height = dotSizeSource;
            }
        }

        double scaledSpacing = settings.GridSpacing * scale;
        double offsetX = TransformMatrix.M31 % scaledSpacing;
        double offsetY = TransformMatrix.M32 % scaledSpacing;
        if (offsetX < 0) offsetX += scaledSpacing;
        if (offsetY < 0) offsetY += scaledSpacing;

        _gridBrush.DestinationRect = new RelativeRect(offsetX, offsetY, scaledSpacing, scaledSpacing, RelativeUnit.Absolute);
        gridCanvas.Background = _gridBrush;
    }
}
