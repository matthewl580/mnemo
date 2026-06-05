using Avalonia;

namespace Mnemo.UI.Modules.Notes.Views;

/// <summary>Viewport zoom and scroll math for the notes editor (view-owned, not DI).</summary>
public sealed class NotesEditorCamera
{
    public double Zoom { get; private set; } = 1.0;

    public void Reset() => Zoom = 1.0;

    public double ClampZoom(double zoom) =>
        Math.Clamp(zoom, Notes.Services.NotesEditorConstants.MinEditorZoom, Notes.Services.NotesEditorConstants.MaxEditorZoom);

    public bool TryApplyWheelZoom(double wheelDeltaY, out double newZoom)
    {
        var zoomFactor = wheelDeltaY > 0 ? 1.1 : 0.9;
        newZoom = ClampZoom(Zoom * zoomFactor);
        if (Math.Abs(newZoom - Zoom) < 1e-6)
            return false;
        Zoom = newZoom;
        return true;
    }

    /// <summary>Maps a viewport cursor position to document coordinates before zoom changes.</summary>
    public static Point ContentPointFromScroll(
        double zoom,
        Vector scrollOffset,
        double hostX,
        Point cursorInViewport)
    {
        var contentPoint = new Point(
            scrollOffset.X + cursorInViewport.X,
            scrollOffset.Y + cursorInViewport.Y);
        return new Point(
            (contentPoint.X - hostX) / zoom,
            contentPoint.Y / zoom);
    }

    /// <summary>Scroll offset that keeps <paramref name="docPoint"/> under <paramref name="cursorInViewport"/> after zoom.</summary>
    public static Vector ScrollOffsetForZoomAnchor(
        double newZoom,
        Point docPoint,
        Point cursorInViewport,
        double newHostX)
    {
        return new Vector(
            newHostX + docPoint.X * newZoom - cursorInViewport.X,
            docPoint.Y * newZoom - cursorInViewport.Y);
    }

    public static double HostCenteringX(double outerWidth, double extentWidth) =>
        Math.Max(0, (outerWidth - extentWidth) / 2);

    public static Vector ClampScrollOffset(Vector offset, Size extent, Size viewport)
    {
        if (viewport.Width <= 1 || viewport.Height <= 1 || extent.Width <= 1 || extent.Height <= 1)
            return offset;

        var maxX = Math.Max(0, extent.Width - viewport.Width);
        var maxY = Math.Max(0, extent.Height - viewport.Height);
        return new Vector(
            Math.Clamp(offset.X, 0, maxX),
            Math.Clamp(offset.Y, 0, maxY));
    }

    public static Size ZoomedExtent(Size naturalSize, double zoom) =>
        new(naturalSize.Width * zoom, naturalSize.Height * zoom);

    public static Size ViewportSize(ScrollViewerDimensions dims)
    {
        var width = dims.ViewportWidth > 1 ? dims.ViewportWidth : dims.BoundsWidth;
        var height = dims.ViewportHeight > 1 ? dims.ViewportHeight : dims.BoundsHeight;
        return new Size(Math.Max(1, width), Math.Max(1, height));
    }

    public static bool NearlySameSize(Size a, Size b) =>
        Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;
}

public readonly struct ScrollViewerDimensions
{
    public double ViewportWidth { get; init; }
    public double ViewportHeight { get; init; }
    public double BoundsWidth { get; init; }
    public double BoundsHeight { get; init; }
}
