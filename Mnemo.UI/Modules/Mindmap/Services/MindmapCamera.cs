using Avalonia;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>
/// Viewport transform for the mindmap canvas: zoom, pan, and content/screen coordinate conversion.
/// </summary>
public sealed class MindmapCamera
{
    public const double MinScale = 0.1;
    public const double MaxScale = 5.0;

    public Matrix Transform { get; private set; } = Matrix.Identity;

    public double Scale => GetScaleFromMatrix(Transform);

    public static double GetScaleFromMatrix(Matrix matrix)
    {
        double scale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        return scale <= 0 ? 1.0 : scale;
    }

    public void SetTransform(Matrix matrix) => Transform = matrix;

    /// <summary>Zoom toward a screen-space anchor (e.g. pointer position on the canvas).</summary>
    public bool TryZoomAt(Point screenAnchor, double zoomFactor)
    {
        double det = Transform.M11 * Transform.M22 - Transform.M12 * Transform.M21;
        if (Math.Abs(det) < 1e-10)
            Transform = Matrix.Identity;

        double currentScale = Scale;
        double newScale = Math.Clamp(currentScale * zoomFactor, MinScale, MaxScale);
        zoomFactor = newScale / currentScale;
        if (Math.Abs(zoomFactor - 1.0) < 1e-6)
            return false;

        var canvasPointBefore = ScreenToContent(screenAnchor);
        Transform = Transform * Matrix.CreateScale(zoomFactor, zoomFactor);
        var screenPointAfter = ContentToScreen(canvasPointBefore);
        var offset = screenAnchor - screenPointAfter;
        Transform = Transform * Matrix.CreateTranslation(offset.X, offset.Y);
        return true;
    }

    public void RecenterOnContentCenter(double centerX, double centerY, double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double scale = Scale;
        double offsetX = viewportWidth / 2 - centerX * scale;
        double offsetY = viewportHeight / 2 - centerY * scale;
        Transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);
    }

    public void CenterOnContentPoint(Point contentPoint, double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double scale = Scale;
        double offsetX = viewportWidth / 2.0 - contentPoint.X * scale;
        double offsetY = viewportHeight / 2.0 - contentPoint.Y * scale;
        Transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY);
    }

    public void PanByScreenDelta(double screenDx, double screenDy) =>
        Transform = Transform * Matrix.CreateTranslation(screenDx, screenDy);

    public Point ScreenToContent(Point screen) => screen * Transform.Invert();

    public Point ContentToScreen(Point content) => content * Transform;
}
