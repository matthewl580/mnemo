using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mnemo.UI.Components.BlockEditor;
using Mnemo.UI.Modules.Notes.ViewModels;

namespace Mnemo.UI.Modules.Notes.Views;

public partial class NotesView
{
    private readonly NotesEditorCamera _editorCamera = new();

    private bool _editorZoomHandlersWired;
    private bool _editorScrollPanHandlersWired;
    private bool _editorScrollPanning;
    private Point _editorPanLastPosition;
    private IPointer? _editorPanPointer;
    private bool _editorScrollbarThumbDragging;
    private IPointer? _editorScrollbarThumbPointer;
    private Control? _editorScrollbarDragTrack;
    private Thumb? _editorScrollbarDragThumb;
    private double _editorScrollbarDragPointerOffsetY;
    private bool _editorScrollbarDragSyncScheduled;
    private double _editorScrollbarDragRatio;
    private Size _lastEditorDocumentSizeForZoom;

    public void ResetEditorView()
    {
        EndEditorScrollPanIfNeeded();
        _editorCamera.Reset();
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll != null)
            scroll.Offset = default;
        _lastEditorDocumentSizeForZoom = default;
        ApplyEditorCameraZoom();
    }

    private void SetupEditorScrollPanAndGutter()
    {
        if (_editorScrollPanHandlersWired) return;
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null) return;
        scroll.AddHandler(PointerPressedEvent, OnEditorScrollViewerPointerPressed, RoutingStrategies.Tunnel);
        scroll.AddHandler(PointerMovedEvent, OnEditorScrollViewerPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.AddHandler(PointerReleasedEvent, OnEditorScrollViewerPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        scroll.PointerCaptureLost += OnEditorScrollViewerPointerCaptureLost;
        _editorScrollPanHandlersWired = true;
    }

    private void TeardownEditorScrollPanAndGutter()
    {
        if (!_editorScrollPanHandlersWired) return;
        EndEditorScrollPanIfNeeded();
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll != null)
        {
            scroll.RemoveHandler(PointerPressedEvent, OnEditorScrollViewerPointerPressed);
            scroll.RemoveHandler(PointerMovedEvent, OnEditorScrollViewerPointerMoved);
            scroll.RemoveHandler(PointerReleasedEvent, OnEditorScrollViewerPointerReleased);
            scroll.PointerCaptureLost -= OnEditorScrollViewerPointerCaptureLost;
        }
        _editorScrollPanHandlersWired = false;
    }

    private void SetupEditorCameraZoom()
    {
        if (_editorZoomHandlersWired) return;
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        if (scroll == null || doc == null) return;
        scroll.AddHandler(PointerWheelChangedEvent, OnEditorScrollViewerPointerWheelChanged, RoutingStrategies.Tunnel);
        scroll.ScrollChanged += OnEditorScrollViewerScrollChanged;
        scroll.SizeChanged += OnEditorScrollViewerSizeChanged;
        doc.LayoutUpdated += OnEditorDocumentLayoutUpdated;
        _editorZoomHandlersWired = true;
        Dispatcher.UIThread.Post(ApplyEditorCameraZoom, DispatcherPriority.Loaded);
    }

    private void TeardownEditorCameraZoom()
    {
        if (!_editorZoomHandlersWired) return;
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        scroll?.RemoveHandler(PointerWheelChangedEvent, OnEditorScrollViewerPointerWheelChanged);
        if (scroll != null)
        {
            scroll.ScrollChanged -= OnEditorScrollViewerScrollChanged;
            scroll.SizeChanged -= OnEditorScrollViewerSizeChanged;
        }
        if (doc != null)
            doc.LayoutUpdated -= OnEditorDocumentLayoutUpdated;
        _editorZoomHandlersWired = false;
    }

    private void OnEditorScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null) return;

        var pt = e.GetCurrentPoint(scroll);
        TryBeginEditorScrollbarThumbDrag(e);

        if (pt.Properties.IsMiddleButtonPressed)
        {
            TryBeginEditorScrollPan(scroll, e);
            return;
        }

        if (pt.Properties.IsLeftButtonPressed && (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            TryBeginEditorScrollPan(scroll, e);
            return;
        }

        OnGutterPointerPressed(sender, e);
    }

    private void TryBeginEditorScrollPan(ScrollViewer scroll, PointerPressedEventArgs e)
    {
        _editorScrollPanning = true;
        _editorPanPointer = e.Pointer;
        _editorPanLastPosition = e.GetPosition(scroll);
        e.Pointer.Capture(scroll);
        scroll.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void OnEditorScrollViewerPointerMoved(object? sender, PointerEventArgs e)
    {
        UpdateEditorScrollbarThumbDrag(e);

        if (!_editorScrollPanning) return;
        var scroll = sender as ScrollViewer ?? this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null || !ReferenceEquals(e.Pointer.Captured, scroll)) return;

        var pos = e.GetPosition(scroll);
        var d = pos - _editorPanLastPosition;
        _editorPanLastPosition = pos;
        scroll.Offset -= new Vector(d.X, d.Y);
        ClampEditorScrollOffset();
    }

    private void OnEditorScrollViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_editorScrollbarThumbDragging && ReferenceEquals(e.Pointer, _editorScrollbarThumbPointer))
            EndEditorScrollbarThumbDrag();

        var scroll = sender as ScrollViewer ?? this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null || !_editorScrollPanning) return;
        if (!ReferenceEquals(e.Pointer.Captured, scroll)) return;

        _editorScrollPanning = false;
        _editorPanPointer = null;
        e.Pointer.Capture(null);
        scroll.Cursor = Cursor.Default;
    }

    private void OnEditorScrollViewerPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndEditorScrollbarThumbDrag();
        EndEditorScrollPanIfNeeded();
    }

    private void EndEditorScrollPanIfNeeded()
    {
        if (!_editorScrollPanning && _editorPanPointer == null) return;
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        _editorScrollPanning = false;
        _editorPanPointer?.Capture(null);
        _editorPanPointer = null;
        if (scroll != null)
            scroll.Cursor = Cursor.Default;
    }

    private void TryBeginEditorScrollbarThumbDrag(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is not Visual source)
            return;

        var thumb = source as Thumb ?? source.GetVisualAncestors().OfType<Thumb>().FirstOrDefault();
        if (thumb == null)
            return;

        var scrollBar = source as ScrollBar ?? source.GetVisualAncestors().OfType<ScrollBar>().FirstOrDefault();
        if (scrollBar?.Orientation != Orientation.Vertical)
            return;

        if (thumb.GetVisualParent() is not Control track || track.Bounds.Height <= thumb.Bounds.Height)
            return;

        var thumbTop = thumb.TranslatePoint(new Point(0, 0), track);
        if (!thumbTop.HasValue)
            return;

        _editorScrollbarThumbDragging = true;
        _editorScrollbarThumbPointer = e.Pointer;
        _editorScrollbarDragTrack = track;
        _editorScrollbarDragThumb = thumb;
        _editorScrollbarDragPointerOffsetY = e.GetPosition(track).Y - thumbTop.Value.Y;
    }

    private void UpdateEditorScrollbarThumbDrag(PointerEventArgs e)
    {
        if (!_editorScrollbarThumbDragging || !ReferenceEquals(e.Pointer, _editorScrollbarThumbPointer))
            return;

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndEditorScrollbarThumbDrag();
            return;
        }

        if (_editorScrollbarDragTrack == null || _editorScrollbarDragThumb == null)
            return;

        var usableTrack = _editorScrollbarDragTrack.Bounds.Height - _editorScrollbarDragThumb.Bounds.Height;
        if (usableTrack <= 1)
            return;

        var pointerY = e.GetPosition(_editorScrollbarDragTrack).Y;
        var thumbTop = Math.Clamp(pointerY - _editorScrollbarDragPointerOffsetY, 0, usableTrack);
        _editorScrollbarDragRatio = thumbTop / usableTrack;

        if (_editorScrollbarDragSyncScheduled)
            return;

        _editorScrollbarDragSyncScheduled = true;
        Dispatcher.UIThread.Post(ApplyEditorScrollbarThumbDragRatio, DispatcherPriority.Input);
    }

    private void ApplyEditorScrollbarThumbDragRatio()
    {
        _editorScrollbarDragSyncScheduled = false;
        if (!_editorScrollbarThumbDragging)
            return;

        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null)
            return;

        var maxY = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var y = Math.Clamp(_editorScrollbarDragRatio, 0, 1) * maxY;
        if (Math.Abs(scroll.Offset.Y - y) > 0.5)
            scroll.Offset = new Vector(scroll.Offset.X, y);
    }

    private void EndEditorScrollbarThumbDrag()
    {
        _editorScrollbarThumbDragging = false;
        _editorScrollbarThumbPointer = null;
        _editorScrollbarDragTrack = null;
        _editorScrollbarDragThumb = null;
        _editorScrollbarDragPointerOffsetY = 0;
        _editorScrollbarDragRatio = 0;
    }

    private void OnGutterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount > 1) return;
        if (IsScrollBarChrome(e.Source as Visual)) return;

        var editor = this.FindControl<BlockEditor>("NoteBlockEditor");
        if (editor == null) return;

        var posInEditor = e.GetPosition(editor);
        var editorBounds = new Rect(0, 0, editor.Bounds.Width, editor.Bounds.Height);
        if (editorBounds.Contains(posInEditor)) return;

        if (posInEditor.Y < 0 || posInEditor.Y > editor.Bounds.Height) return;

        editor.ArmExternalBoxSelect(posInEditor, e.Pointer);
    }

    private static bool IsScrollBarChrome(Visual? source)
    {
        if (source == null)
            return false;

        return source is ScrollBar or Thumb
            || source.GetVisualAncestors().Any(a => a is ScrollBar or Thumb);
    }

    private void OnEditorDocumentLayoutUpdated(object? sender, EventArgs e)
    {
        SyncEditorDocumentLayoutWidth();
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        if (doc == null) return;
        var s = GetEditorDocumentNaturalSize(doc);
        if (s.Width <= 1 || s.Height <= 1) return;
        if (NotesEditorCamera.NearlySameSize(s, _lastEditorDocumentSizeForZoom)) return;
        _lastEditorDocumentSizeForZoom = s;
        ApplyEditorCameraZoom();
    }

    private void OnEditorScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var mods = e.KeyModifiers;
        if ((mods & KeyModifiers.Control) == 0 && (mods & KeyModifiers.Meta) == 0)
            return;

        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        var outer = this.FindControl<Border>("EditorScrollWidthHost");
        if (scroll == null || outer == null) return;

        var oldZoom = _editorCamera.Zoom;
        if (!_editorCamera.TryApplyWheelZoom(e.Delta.Y, out var newZoom))
        {
            e.Handled = true;
            return;
        }

        var viewport = GetEditorViewportSize(scroll);
        var cursorInViewport = e.GetPosition(scroll);
        cursorInViewport = new Point(
            Math.Clamp(cursorInViewport.X, 0, viewport.Width),
            Math.Clamp(cursorInViewport.Y, 0, viewport.Height));

        var oldExtent = GetEditorZoomedExtentSize(oldZoom);
        var oldOuterWidth = outer.Width > 1 ? outer.Width : Math.Max(oldExtent.Width, viewport.Width);
        var oldHostX = NotesEditorCamera.HostCenteringX(oldOuterWidth, oldExtent.Width);
        var docPoint = NotesEditorCamera.ContentPointFromScroll(
            oldZoom, scroll.Offset, oldHostX, cursorInViewport);

        ApplyEditorCameraZoom();
        scroll.UpdateLayout();

        var newExtent = GetEditorZoomedExtentSize(newZoom);
        var newOuterWidth = outer.Width > 1 ? outer.Width : Math.Max(newExtent.Width, viewport.Width);
        var newHostX = NotesEditorCamera.HostCenteringX(newOuterWidth, newExtent.Width);
        scroll.Offset = NotesEditorCamera.ScrollOffsetForZoomAnchor(newZoom, docPoint, cursorInViewport, newHostX);
        ClampEditorScrollOffset();
        e.Handled = true;
    }

    private Size GetEditorDocumentNaturalSize(Panel doc)
    {
        Size s;
        var d = doc.DesiredSize;
        if (d.Width > 1 && d.Height > 1)
            s = d;
        else
        {
            var b = doc.Bounds.Size;
            if (b.Width <= 1 || b.Height <= 1)
                return b;
            s = b;
        }

        if (DataContext is NotesViewModel vm && vm.EditorMaxWidth > 1)
        {
            var layoutWidth = !double.IsNaN(doc.Width) && doc.Width > 1 ? doc.Width : vm.EditorMaxWidth;
            var minW = layoutWidth + doc.Margin.Left + doc.Margin.Right;
            if (s.Width < minW - 0.5)
                s = new Size(minW, s.Height);
        }

        return s;
    }

    private void ApplyEditorCameraZoom()
    {
        var host = this.FindControl<EditorZoomHost>("EditorZoomExtentHost");
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        if (host == null || doc == null) return;
        SyncEditorDocumentLayoutWidth();
        var s = GetEditorDocumentNaturalSize(doc);
        if (s.Width <= 1 || s.Height <= 1)
        {
            ClearEditorScrollWidthHostSizing();
            return;
        }

        host.Zoom = _editorCamera.Zoom;
        host.LayoutWidth = s.Width;
        SyncEditorScrollWidthHost();
        Dispatcher.UIThread.Post(ClampEditorScrollOffset, DispatcherPriority.Loaded);
    }

    private void OnEditorScrollViewerSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SyncEditorDocumentLayoutWidth();
        _lastEditorDocumentSizeForZoom = default;
        ApplyEditorCameraZoom();
        SyncEditorScrollWidthHost();
        Dispatcher.UIThread.Post(ClampEditorScrollOffset, DispatcherPriority.Loaded);
    }

    private void OnEditorScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentDelta == default && e.ViewportDelta == default)
            return;
        Dispatcher.UIThread.Post(ClampEditorScrollOffset, DispatcherPriority.Loaded);
    }

    private void SyncEditorScrollWidthHost()
    {
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        var outer = this.FindControl<Border>("EditorScrollWidthHost");
        if (scroll == null || outer == null) return;

        var extent = GetEditorZoomedExtentSize(_editorCamera.Zoom);
        var cw = extent.Width;
        var ch = extent.Height;
        if (cw <= 1 || ch <= 1 || double.IsNaN(cw) || double.IsNaN(ch))
        {
            ClearEditorScrollWidthHostSizing();
            return;
        }

        var vw = scroll.Viewport.Width;
        if (vw <= 1)
            vw = scroll.Bounds.Width;
        var targetW = vw > 1 ? Math.Max(cw, vw) : cw;
        if (double.IsNaN(outer.Width) || Math.Abs(outer.Width - targetW) > 0.5)
            outer.Width = targetW;
        if (outer.IsSet(Control.HeightProperty))
            outer.ClearValue(Control.HeightProperty);
    }

    private Size GetEditorZoomedExtentSize(double zoom)
    {
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        if (doc == null)
            return default;

        var s = GetEditorDocumentNaturalSize(doc);
        return NotesEditorCamera.ZoomedExtent(s, zoom);
    }

    private static Size GetEditorViewportSize(ScrollViewer scroll)
    {
        return NotesEditorCamera.ViewportSize(new ScrollViewerDimensions
        {
            ViewportWidth = scroll.Viewport.Width,
            ViewportHeight = scroll.Viewport.Height,
            BoundsWidth = scroll.Bounds.Width,
            BoundsHeight = scroll.Bounds.Height
        });
    }

    private bool SyncEditorDocumentLayoutWidth()
    {
        if (DataContext is not NotesViewModel vm || vm.EditorMaxWidth <= 1)
            return false;

        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        var doc = this.FindControl<Panel>("EditorDocumentPanel");
        var host = this.FindControl<EditorZoomHost>("EditorZoomExtentHost");
        if (scroll == null || doc == null || host == null)
            return false;

        var vw = GetEditorViewportSize(scroll).Width;
        if (vw <= 1)
            return false;

        var available = Math.Max(1, vw - doc.Margin.Left - doc.Margin.Right);
        var width = Math.Min(vm.EditorMaxWidth, available);
        host.LayoutWidth = width + doc.Margin.Left + doc.Margin.Right;
        if (Math.Abs(doc.Width - width) <= 0.5)
            return false;

        doc.Width = width;
        return true;
    }

    private void ResetEditorHorizontalOffset()
    {
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null) return;
        if (Math.Abs(scroll.Offset.X) > 0.01)
            scroll.Offset = new Vector(0, scroll.Offset.Y);
    }

    private void ClearEditorScrollWidthHostSizing()
    {
        var outer = this.FindControl<Border>("EditorScrollWidthHost");
        if (outer == null) return;
        outer.ClearValue(Control.WidthProperty);
        outer.ClearValue(Control.HeightProperty);
    }

    private void ClampEditorScrollOffset()
    {
        var scroll = this.FindControl<ScrollViewer>("EditorScrollViewer");
        if (scroll == null) return;

        scroll.Offset = NotesEditorCamera.ClampScrollOffset(scroll.Offset, scroll.Extent, scroll.Viewport);
    }
}
