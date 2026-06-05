using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Collections;
using Mnemo.Core.Enums;
using Mnemo.Core.Models.Mindmap;
using Mnemo.Core.Models.Keybinds;
using Mnemo.Core.Services;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;
using Mnemo.UI.Modules.Mindmap.ViewModels;
using Mnemo.UI.Services;

namespace Mnemo.UI.Modules.Mindmap.Views;

public partial class MindmapView : UserControl
{
    public static readonly DirectProperty<MindmapView, bool> ShowCollapsedNodesOnMinimapBindingProperty =
        AvaloniaProperty.RegisterDirect<MindmapView, bool>(
            nameof(ShowCollapsedNodesOnMinimapBinding),
            o => o.ShowCollapsedNodesOnMinimapBinding,
            (o, v) => o.ShowCollapsedNodesOnMinimapBinding = v);

    private bool _showCollapsedNodesOnMinimapBinding;
    public bool ShowCollapsedNodesOnMinimapBinding
    {
        get => _showCollapsedNodesOnMinimapBinding;
        private set => SetAndRaise(ShowCollapsedNodesOnMinimapBindingProperty, ref _showCollapsedNodesOnMinimapBinding, value);
    }

    private double _lastMainCanvasWidth;
    private double _lastMainCanvasHeight;
    private bool _isDragging;
    private bool _isPanning;
    private bool _isSelecting;
    private bool _addToSelectionOnBoxSelect;
    private bool _hasMovedNodeDuringDrag;
    private Point _selectionStart;
    private Point _selectionStartInCanvas; // content-space start, fixed at press so zoom doesn't break hit-test
    private Point _selectionCurrentInCanvas; // content-space current position, updated as mouse moves
    private Point _lastPointerPosition;
    private NodeViewModel? _draggedNode;
    private MindmapModel? _moveBeforeSnapshot; // captured at drag start for correct undo
    private Border? _selectionBox;

    private const double ClickDragThreshold = 5.0;
    private const double EdgeHitThreshold = 20;
    private const int EdgeHoverThrottleMs = 32; // ~30fps max for expensive GetDistanceToCurve loop

    private DispatcherTimer? _easeTimer;
    private long _lastEdgeHoverUpdateTicks = 0;

    /// <summary>Avalonia <see cref="PointerPressedEventArgs.ClickCount"/> is not always 2 on the second press; we also detect a quick second tap on the same edge.</summary>
    private string? _edgeDoubleTapPendingId;
    private long _edgeDoubleTapPendingTicks;
    private const long EdgeDoubleTapMaxDeltaMs = 650;

    public MindmapView()
    {
        InitializeComponent();

        var canvas = this.FindControl<Panel>("MainCanvas");
        if (canvas != null)
        {
            canvas.PointerWheelChanged += OnCanvasPointerWheelChanged;
            canvas.SizeChanged += OnMainCanvasSizeChanged;
        }

        _selectionBox = this.FindControl<Border>("SelectionBox");

        DataContextChanged += OnDataContextChanged;

        Loaded += OnViewLoaded;
        Unloaded += OnViewUnloaded;
    }

    private void OnViewLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateMinimap();
        UpdateMinimapVisibility();
        Loaded -= OnViewLoaded;
    }

    private void OnViewUnloaded(object? sender, RoutedEventArgs e)
    {
        _easeTimer?.Stop();
        _easeTimer = null;
    }

    private MindmapViewModel? _boundMindmapVm;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_boundMindmapVm != null)
        {
            _boundMindmapVm.RecenterRequested -= OnRecenterRequested;
            _boundMindmapVm.FocusEdgeLabelRequested -= OnFocusEdgeLabelRequested;
            _boundMindmapVm.ExportRequested -= OnExportRequested;
            _boundMindmapVm.Nodes.CollectionChanged -= OnNodesCollectionChanged;
            _boundMindmapVm.PropertyChanged -= OnMindmapViewModelPropertyChanged;
            _boundMindmapVm.CanvasSettings.PropertyChanged -= OnCanvasSettingsChanged;
            _boundMindmapVm = null;
        }
        if (DataContext is MindmapViewModel vm)
        {
            _boundMindmapVm = vm;
            vm.RecenterRequested += OnRecenterRequested;
            vm.FocusEdgeLabelRequested += OnFocusEdgeLabelRequested;
            vm.ExportRequested += OnExportRequested;
            vm.Nodes.CollectionChanged += OnNodesCollectionChanged;
            vm.PropertyChanged += OnMindmapViewModelPropertyChanged;
            vm.CanvasSettings.PropertyChanged += OnCanvasSettingsChanged;
            vm.ZoomLevel = GetScaleFromMatrix(TransformMatrix);
            ShowCollapsedNodesOnMinimapBinding = vm.ShowCollapsedNodesOnMinimap;
            _gridBrush = null;
            UpdateGrid();
        }
        else
        {
            ShowCollapsedNodesOnMinimapBinding = false;
        }
    }

    private void OnMindmapViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MindmapViewModel.ZoomLevel) && DataContext is MindmapViewModel vm)
        {
            ApplyZoomFromVm(vm.ZoomLevel);
        }
        else if (e.PropertyName == nameof(MindmapViewModel.MinimapVisibilityMode))
        {
            UpdateMinimapVisibility();
        }
        else if (e.PropertyName == nameof(MindmapViewModel.ShowCollapsedNodesOnMinimap)
                 && sender is MindmapViewModel changedVm)
        {
            ShowCollapsedNodesOnMinimapBinding = changedVm.ShowCollapsedNodesOnMinimap;
            UpdateMinimap();
        }
        else if (e.PropertyName == nameof(MindmapViewModel.MinimapVisibilityMode))
        {
            UpdateMinimapVisibility();
        }
    }

    private void OnNodesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Subscribe/unsubscribe node property changes so minimap updates when nodes move
        if (e.NewItems != null)
        {
            foreach (NodeViewModel node in e.NewItems)
                node.PropertyChanged += OnNodePropertyChangedForMinimap;
        }
        if (e.OldItems != null)
        {
            foreach (NodeViewModel node in e.OldItems)
                node.PropertyChanged -= OnNodePropertyChangedForMinimap;
        }

        // Auto-recenter after nodes are loaded, but not when restoring state (undo/redo) so node positions update without camera snap
        if (DataContext is MindmapViewModel vm && vm.Nodes.Any())
        {
            if (vm.SuppressRecenterOnNextCollectionChange)
            {
                vm.SuppressRecenterOnNextCollectionChange = false;
                UpdateMinimap();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await Task.Delay(50); // Allow layout to complete
                    RecenterView();
                    UpdateMinimap();
                });
            }
        }
    }

    private void OnNodePropertyChangedForMinimap(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeViewModel.X) or nameof(NodeViewModel.Y)
                            or nameof(NodeViewModel.Width) or nameof(NodeViewModel.Height)
                            or nameof(NodeViewModel.ActualWidth) or nameof(NodeViewModel.ActualHeight))
        {
            UpdateMinimap();
        }
    }


    private void OnFocusEdgeLabelRequested(EdgeViewModel edge) => FocusEdgeLabelBox(edge);

    private void OnRecenterRequested(object? sender, EventArgs e)
    {
        RecenterView();
    }

    private void OnMainCanvasSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            _lastMainCanvasWidth = e.NewSize.Width;
            _lastMainCanvasHeight = e.NewSize.Height;
            UpdateTransform();
        }
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed) return;
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas != null && TryHandleEdgePressFromCanvas(e, mainCanvas))
            return;

        bool isShiftPressed = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        // When ModifierBehaviour is "Selecting": Shift + drag = select, no modifier = pan. When "Panning": opposite.
        bool modifierMeansSelect = (DataContext as MindmapViewModel)?.CanvasSettings.ModifierBehaviour == "Selecting";
        bool doPan = modifierMeansSelect ? !isShiftPressed : isShiftPressed;
        bool doSelect = !doPan;
        if (DataContext is MindmapViewModel vm && !vm.IsEditingEnabled)
        {
            doPan = true;
            doSelect = false;
        }

        // Only when click was on empty space (node handler sets e.Handled when clicking a node)
        if (!e.Handled)
        {
            // Move focus to canvas so the node TextBox loses focus (deselects visually)
            if (sender is Control focusTarget)
                focusTarget.Focus();

            if (doPan)
            {
                _isPanning = true;
                _isSelecting = false;
                // Don't deselect here - wait for release to see if it was a click or drag
            }
            else
            {
                _isPanning = false;
                _isSelecting = true;
                if (DataContext is MindmapViewModel viewModel) viewModel.SelectedEdge = null;
                _addToSelectionOnBoxSelect = e.KeyModifiers.HasFlag(KeyModifiers.Control);
                _selectionStart = mainCanvas != null ? e.GetPosition(mainCanvas) : e.GetPosition(this);
                var inv = TransformMatrix.Invert();
                _selectionStartInCanvas = _selectionStart * inv;
                _selectionCurrentInCanvas = _selectionStartInCanvas; // Initialize to start position
                UpdateSelectionBox(_selectionStart, _selectionStart);
                if (_selectionBox != null) _selectionBox.IsVisible = true;
            }
        }
        else
        {
            _isPanning = false;
            _isSelecting = false;
        }

        _lastPointerPosition = e.GetPosition(this);
    }

    private bool TryHandleEdgePressFromCanvas(PointerPressedEventArgs e, Panel mainCanvas)
    {
        if (DataContext is not MindmapViewModel vm || !vm.IsEditingEnabled) return false;

        var pos = e.GetPosition(mainCanvas);
        var contentPoint = pos * TransformMatrix.Invert();
        const double labelHalfW = 30;
        const double labelHalfH = 11;

        EdgeViewModel? hitEdge = null;
        double bestDist = double.MaxValue;
        foreach (var edge in vm.Edges)
        {
            if (edge.Label != null)
            {
                var cx = edge.CenterPoint.X;
                var cy = edge.CenterPoint.Y;
                if (contentPoint.X >= cx - labelHalfW && contentPoint.X <= cx + labelHalfW
                    && contentPoint.Y >= cy - labelHalfH && contentPoint.Y <= cy + labelHalfH)
                {
                    hitEdge = edge;
                    break;
                }
            }

            var d = edge.GetDistanceToCurve(contentPoint);
            if (d < EdgeHitThreshold && d < bestDist)
            {
                bestDist = d;
                hitEdge = edge;
            }
        }

        if (hitEdge == null) return false;

        e.Handled = true;
        bool activateEdit = e.ClickCount >= 2 || TryConsumeEdgeDoubleActivate(hitEdge);
        if (activateEdit)
            ActivateEdgeLabelEdit(hitEdge);
        else
            vm.SelectedEdge = hitEdge;

        return true;
    }

    private void UpdateSelectionBox(Point start, Point end)
    {
        if (_selectionBox == null) return;

        // Calculate box bounds - one corner is at start (where mouse was pressed)
        // and the opposite corner follows the current mouse position
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        double width = Math.Abs(end.X - start.X);
        double height = Math.Abs(end.Y - start.Y);

        // Position the box using margin instead of transform for cleaner positioning
        _selectionBox.Margin = new Thickness(x, y, 0, 0);
        _selectionBox.Width = width;
        _selectionBox.Height = height;
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MindmapViewModel editVm && !editVm.IsEditingEnabled)
            return;
        if (sender is Control control && control.DataContext is NodeViewModel node)
        {
            if (DataContext is MindmapViewModel vm)
            {
                vm.SelectedEdge = null; // Node selection context; edge panel greyed out
                // Multi-select with Ctrl, otherwise single select
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    foreach (var n in vm.Nodes) n.IsSelected = false;
                }
                node.IsSelected = true;
                _moveBeforeSnapshot = vm.CaptureMoveSnapshot();
            }

            _isDragging = true;
            _hasMovedNodeDuringDrag = false;
            _isPanning = false;
            _draggedNode = node;
            _lastPointerPosition = e.GetPosition(this);
            e.Handled = true;
        }
    }

    private void OnNodePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isDragging && _draggedNode != null)
        {
            var currentPosition = e.GetPosition(this);
            var delta = currentPosition - _lastPointerPosition;
            
            // Adjust delta by current zoom
            double scale = GetScaleFromMatrix(TransformMatrix);
            
            var dx = delta.X / scale;
            var dy = delta.Y / scale;

            if ((Math.Abs(dx) > 1e-6 || Math.Abs(dy) > 1e-6) && DataContext is MindmapViewModel vm)
            {
                _hasMovedNodeDuringDrag = true;
                var selectedNodes = vm.Nodes.Where(n => n.IsSelected).ToList();
                if (selectedNodes.Contains(_draggedNode))
                {
                    foreach (var node in selectedNodes)
                    {
                        node.X += dx;
                        node.Y += dy;
                    }
                }
                else
                {
                    _draggedNode.X += dx;
                    _draggedNode.Y += dy;
                }
            }
            
            _lastPointerPosition = currentPosition;
            if (_hasMovedNodeDuringDrag)
                UpdateMinimap();
            e.Handled = true;
        }
        else if (_isPanning)
        {
            var currentPosition = e.GetPosition(this);
            var delta = currentPosition - _lastPointerPosition;

            TransformMatrix = TransformMatrix * Matrix.CreateTranslation(delta.X, delta.Y);
            UpdateTransform();
            
            _lastPointerPosition = currentPosition;
            e.Handled = true;
        }
        else if (_isSelecting)
        {
            var mainCanvas = this.FindControl<Panel>("MainCanvas");
            var currentPosition = mainCanvas != null ? e.GetPosition(mainCanvas) : e.GetPosition(this);

            if (DataContext is MindmapViewModel vm)
            {
                var inv = TransformMatrix.Invert();
                _selectionCurrentInCanvas = currentPosition * inv;

                UpdateNodeSelection(vm);
            }
            e.Handled = true;
        }
        else
        {
            UpdateEdgeHoverAndCursor(e);
        }
    }

    private void UpdateEdgeHoverAndCursor(PointerEventArgs e)
    {
        var mainCanvas = this.FindControl<Panel>("MainCanvas");
        if (mainCanvas == null || DataContext is not MindmapViewModel vm) return;
        var pos = e.GetPosition(mainCanvas);
        var contentPoint = pos * TransformMatrix.Invert();

        foreach (var node in vm.Nodes)
        {
            if (contentPoint.X >= node.X && contentPoint.X <= node.X + node.ActualWidth &&
                contentPoint.Y >= node.Y && contentPoint.Y <= node.Y + node.ActualHeight)
            {
                mainCanvas.Cursor = null;
                vm.SetHoveredEdge(null);
                return;
            }
        }

        long now = Environment.TickCount64;
        if (now - _lastEdgeHoverUpdateTicks < EdgeHoverThrottleMs)
            return;
        _lastEdgeHoverUpdateTicks = now;

        const double labelHalfW = 30;
        const double labelHalfH = 11;
        if (vm.IsEditingEnabled)
        {
            foreach (var edge in vm.Edges)
            {
                var d = edge.GetDistanceToCurve(contentPoint);
                if (d < EdgeHitThreshold)
                {
                    mainCanvas.Cursor = new Cursor(StandardCursorType.Hand);
                    vm.SetHoveredEdge(edge.Id);
                    return;
                }
                if (edge.Label != null)
                {
                    var cx = edge.CenterPoint.X;
                    var cy = edge.CenterPoint.Y;
                    if (contentPoint.X >= cx - labelHalfW && contentPoint.X <= cx + labelHalfW
                        && contentPoint.Y >= cy - labelHalfH && contentPoint.Y <= cy + labelHalfH)
                    {
                        mainCanvas.Cursor = new Cursor(StandardCursorType.Hand);
                        vm.SetHoveredEdge(edge.Id);
                        return;
                    }
                }
            }
        }

        mainCanvas.Cursor = null;
        vm.SetHoveredEdge(null);
    }

    private void UpdateNodeSelection(MindmapViewModel vm)
    {
        var minX = Math.Min(_selectionStartInCanvas.X, _selectionCurrentInCanvas.X);
        var maxX = Math.Max(_selectionStartInCanvas.X, _selectionCurrentInCanvas.X);
        var minY = Math.Min(_selectionStartInCanvas.Y, _selectionCurrentInCanvas.Y);
        var maxY = Math.Max(_selectionStartInCanvas.Y, _selectionCurrentInCanvas.Y);

        var contentRect = new Rect(minX, minY, maxX - minX, maxY - minY);
        
        // Convert content rect back to screen space for visual selection box
        var topLeft = new Point(contentRect.X, contentRect.Y) * TransformMatrix;
        var bottomRight = new Point(contentRect.Right, contentRect.Bottom) * TransformMatrix;
        UpdateSelectionBox(topLeft, bottomRight);
        
        foreach (var node in vm.Nodes)
        {
            var nodeRect = new Rect(node.X, node.Y, node.ActualWidth, node.ActualHeight);
            bool inBox = contentRect.Intersects(nodeRect);
            node.IsSelected = _addToSelectionOnBoxSelect ? (node.IsSelected || inBox) : inBox;
        }
    }

    private void UpdateSelectionBoxFromContentSpace()
    {
        if (DataContext is not MindmapViewModel vm) return;
        
        // Re-compute visual box from stored content-space coordinates
        // Both start and current are already in content space and don't change during zoom
        UpdateNodeSelection(vm);
    }

    private async void OnNodePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        try
        {
        if (_isDragging && _draggedNode != null && DataContext is MindmapViewModel vm)
        {
            if (_hasMovedNodeDuringDrag)
            {
                if (_moveBeforeSnapshot != null)
                {
                    var selectedNodes = vm.Nodes.Where(n => n.IsSelected).ToList();
                    var moves = selectedNodes.Contains(_draggedNode)
                        ? selectedNodes.Select(n => (n, n.X, n.Y)).ToList()
                        : new List<(NodeViewModel node, double x, double y)> { (_draggedNode, _draggedNode.X, _draggedNode.Y) };
                    await vm.UpdateNodesPositionAsync(_moveBeforeSnapshot, moves);
                }
                else
                {
                    var selectedNodes = vm.Nodes.Where(n => n.IsSelected).ToList();
                    if (selectedNodes.Contains(_draggedNode))
                    {
                        foreach (var node in selectedNodes)
                            await vm.UpdateNodePositionAsync(node, node.X, node.Y);
                    }
                    else
                        await vm.UpdateNodePositionAsync(_draggedNode, _draggedNode.X, _draggedNode.Y);
                }
            }
            _moveBeforeSnapshot = null;
            _isDragging = false;
            _draggedNode = null;
            e.Handled = true;
        }
        
        if (_isSelecting)
        {
            _isSelecting = false;
            if (_selectionBox != null) _selectionBox.IsVisible = false;
            e.Handled = true;
        }
        
        if (_isPanning)
        {
            _isPanning = false;
            e.Handled = true;
        }
        }
        catch (Exception ex)
        {
            var logger = (Application.Current as App)?.Services?.GetService(typeof(ILoggerService)) as ILoggerService;
            logger?.Error(nameof(MindmapView), "Error in pointer released handler", ex);
        }
    }

    private void FocusEdgeLabelBox(EdgeViewModel edge)
    {
        var layer = this.FindControl<ItemsControl>("EdgeHitLayer");
        if (layer == null) return;
        var container = layer.GetVisualDescendants().FirstOrDefault(v => v is Control c && c.DataContext == edge) as Control;
        if (container != null)
        {
            var box = container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (box != null)
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.CaretIndex = box.Text?.Length ?? 0;
                }, DispatcherPriority.Loaded);
        }
    }

    private bool TryConsumeEdgeDoubleActivate(EdgeViewModel edge)
    {
        long now = Environment.TickCount64;
        var id = edge.Id;
        if (_edgeDoubleTapPendingId != id)
        {
            _edgeDoubleTapPendingId = id;
            _edgeDoubleTapPendingTicks = now;
            return false;
        }

        long dt = now - _edgeDoubleTapPendingTicks;
        if (dt >= 0 && dt < EdgeDoubleTapMaxDeltaMs)
        {
            _edgeDoubleTapPendingId = null;
            return true;
        }

        _edgeDoubleTapPendingTicks = now;
        return false;
    }

    private void ActivateEdgeLabelEdit(EdgeViewModel edge)
    {
        if (DataContext is not MindmapViewModel vm || !vm.IsEditingEnabled) return;
        vm.EdgeClicked(edge);
        FocusEdgeLabelBox(edge);
    }

    private async void OnNodeTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is TextBox textBox && textBox.DataContext is NodeViewModel node && DataContext is MindmapViewModel vm)
            {
                await vm.UpdateNodeTextAsync(node, textBox.Text ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            var logger = (Application.Current as App)?.Services?.GetService(typeof(ILoggerService)) as ILoggerService;
            logger?.Error(nameof(MindmapView), "Failed to save node text", ex);
        }
    }

    private void OnCanvasPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is MindmapViewModel vm)
            vm.ClearHoverState();
    }

    private void OnMindmapKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MindmapViewModel) return;
        if (Application.Current is not Mnemo.UI.App app || app.Services == null) return;
        if (app.Services.GetService(typeof(IKeyMap)) is not IKeyMap keyMap) return;
        if (app.Services.GetService(typeof(IKeybindActionRouter)) is not IKeybindActionRouter router) return;

        var input = KeybindInputNormalizer.FromKeyEvent(e);
        var r = keyMap.ProcessLocalKeyDown(input, DateTime.UtcNow, SequenceSwallowMode.SwallowOnPrefixAdvance);
        if (!r.Handled) return;

        if (r.CompletedAction && !string.IsNullOrEmpty(r.ActionId) && !router.TryExecute(r.ActionId))
            return;
        e.Handled = true;
    }

    private void OnNodePointerEnter(object? sender, PointerEventArgs e)
    {
        if (sender is Control c && c.DataContext is NodeViewModel node && DataContext is MindmapViewModel vm)
            vm.SetHoveredNode(node.Id, true);
    }

    private void OnNodePointerLeave(object? sender, PointerEventArgs e)
    {
        if (sender is Control c && c.DataContext is NodeViewModel node && DataContext is MindmapViewModel vm)
            vm.SetHoveredNode(node.Id, false);
    }

    private void OnEdgeLabelLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not EdgeViewModel edge || DataContext is not MindmapViewModel vm)
            return;
        vm.CommitEdgeLabel(edge);
    }

    private void OnNodeSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not NodeViewModel node)
            return;
        HandleNodeSizeChanged(control, node, e.NewSize.Width, e.NewSize.Height);
    }

    private void HandleNodeSizeChanged(Control _, NodeViewModel node, double w, double h)
    {
        if (node.Shape == "circle")
        {
            double side = Math.Max(w, h);
            // Enforce equal width and height via the ViewModel so the binding keeps it square.
            // Guard against feedback: only write if the stored value differs meaningfully.
            if (Math.Abs((node.Width ?? 0) - side) > 0.5 || Math.Abs((node.Height ?? 0) - side) > 0.5)
            {
                node.Width = side;
                node.Height = side;
                // SizeChanged will fire again once the binding applies the new size.
                return;
            }
            w = side;
            h = side;
        }

        if (Math.Abs(node.ActualWidth - w) > 0.5 || Math.Abs(node.ActualHeight - h) > 0.5)
        {
            node.ActualWidth = w;
            node.ActualHeight = h;
        }
    }

    private void OnNodeTextBoxTextChanged(object? sender, TextChangedEventArgs e)
    {
        // When text changes on a circle node, release the fixed size so it can re-measure and grow,
        // then OnNodeSizeChanged will re-apply the square constraint at the new content size.
        if (sender is not Control textBox || textBox.DataContext is not NodeViewModel node || node.Shape != "circle")
            return;
        node.Width = null;
        node.Height = null;
    }

}
