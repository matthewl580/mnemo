using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.Services;
using LayoutAlgorithms = Mnemo.Core.Models.Mindmap.LayoutAlgorithms;

namespace Mnemo.UI.Modules.Mindmap.ViewModels;

public partial class MindmapViewModel
{
    [ObservableProperty] private string? _defaultNodeColor;
    [ObservableProperty] private string _defaultNodeShape = "pill";
    [ObservableProperty] private MindmapEdgeKind _defaultEdgeKind = MindmapEdgeKind.Hierarchy;
    [ObservableProperty] private string _defaultEdgeType = EdgeTypes.Solid;
    [ObservableProperty] private EdgeViewModel? _selectedEdge;

    public static IReadOnlyList<string> ToolbarCategories { get; } = new[] { "Edit", "Style", "View" };
    public static IReadOnlyList<string> MinimapVisibilityOptions { get; } = new[] { "Auto", "On", "Off" };
    public static IReadOnlyList<MindmapEdgeKind> EdgeKindOptions { get; } = new[] { MindmapEdgeKind.Hierarchy, MindmapEdgeKind.Link };
    public static IReadOnlyList<string> EdgeTypeIds { get; } = EdgeTypes.All;
    public static IReadOnlyList<string> LayoutAlgorithmIds { get; } =
        new[] { LayoutAlgorithms.TreeVertical, LayoutAlgorithms.TreeHorizontal, LayoutAlgorithms.Radial };

    public NodeViewModel? FirstSelectedNode => Nodes.FirstOrDefault(n => n.IsSelected);
    public bool HasSelectedNodes => Nodes.Any(n => n.IsSelected);

    public string? EffectiveStyleColor => HasSelectedNodes ? FirstSelectedNode?.Color : DefaultNodeColor;
    public string EffectiveStyleShape => HasSelectedNodes ? (FirstSelectedNode?.Shape ?? "pill") : DefaultNodeShape;
    public MindmapEdgeKind EffectiveEdgeKind => SelectedEdge != null ? SelectedEdge.Kind : DefaultEdgeKind;
    public string EffectiveEdgeType => SelectedEdge != null ? SelectedEdge.Type : DefaultEdgeType;

    public MindmapEdgeKind StyleEdgeKindSelected
    {
        get => EffectiveEdgeKind;
        set => SetSelectedEdgeKind(value);
    }

    public string StyleEdgeTypeSelected
    {
        get => EffectiveEdgeType;
        set => SetSelectedEdgeType(value);
    }

    public bool IsEdgeTypeSolid => EffectiveEdgeType == EdgeTypes.Solid;
    public bool IsEdgeTypeDashed => EffectiveEdgeType == EdgeTypes.Dashed;
    public bool IsEdgeTypeDotted => EffectiveEdgeType == EdgeTypes.Dotted;
    public bool IsEdgeTypeDouble => EffectiveEdgeType == EdgeTypes.Double;
    public bool IsEdgeTypeArrow => EffectiveEdgeType == EdgeTypes.Arrow;
    public bool IsEdgeTypeBidirect => EffectiveEdgeType == EdgeTypes.Bidirect;

    public bool IsEditTab => ToolbarCategory == "Edit";
    public bool IsStyleTab => ToolbarCategory == "Style";
    public bool IsViewTab => ToolbarCategory == "View";

    public bool IsLayoutTreeVertical => SelectedLayoutAlgorithm == LayoutAlgorithms.TreeVertical;
    public bool IsLayoutTreeHorizontal => SelectedLayoutAlgorithm == LayoutAlgorithms.TreeHorizontal;
    public bool IsLayoutRadial => SelectedLayoutAlgorithm == LayoutAlgorithms.Radial;

    public bool IsShapeRectangle => EffectiveStyleShape == "rectangle";
    public bool IsShapePill => EffectiveStyleShape == "pill";
    public bool IsShapeCircle => EffectiveStyleShape == "circle";

    partial void OnDefaultNodeColorChanged(string? value)
    {
        if (!HasSelectedNodes) NotifyEffectiveStyleChanged();
    }

    partial void OnDefaultNodeShapeChanged(string value)
    {
        if (!HasSelectedNodes) NotifyEffectiveStyleChanged();
    }

    partial void OnDefaultEdgeTypeChanged(string value)
    {
        if (SelectedEdge == null) NotifyEffectiveEdgeTypeChanged();
    }

    partial void OnSelectedEdgeChanged(EdgeViewModel? value)
    {
        OnPropertyChanged(nameof(EffectiveEdgeKind));
        OnPropertyChanged(nameof(StyleEdgeKindSelected));
        NotifyEffectiveEdgeTypeChanged();
    }

    partial void OnDefaultEdgeKindChanged(MindmapEdgeKind value)
    {
        if (SelectedEdge == null)
        {
            OnPropertyChanged(nameof(EffectiveEdgeKind));
            OnPropertyChanged(nameof(StyleEdgeKindSelected));
        }
    }

    private void NotifyEffectiveStyleChanged()
    {
        OnPropertyChanged(nameof(EffectiveStyleColor));
        OnPropertyChanged(nameof(EffectiveStyleShape));
        OnPropertyChanged(nameof(IsShapeRectangle));
        OnPropertyChanged(nameof(IsShapePill));
        OnPropertyChanged(nameof(IsShapeCircle));
        NotifyEffectiveEdgeTypeChanged();
    }

    private void NotifyEffectiveEdgeTypeChanged()
    {
        OnPropertyChanged(nameof(EffectiveEdgeType));
        OnPropertyChanged(nameof(StyleEdgeTypeSelected));
        OnPropertyChanged(nameof(IsEdgeTypeSolid));
        OnPropertyChanged(nameof(IsEdgeTypeDashed));
        OnPropertyChanged(nameof(IsEdgeTypeDotted));
        OnPropertyChanged(nameof(IsEdgeTypeDouble));
        OnPropertyChanged(nameof(IsEdgeTypeArrow));
        OnPropertyChanged(nameof(IsEdgeTypeBidirect));
    }

    private async void SetSelectedEdgeKind(MindmapEdgeKind? kind)
    {
        try
        {
            if (kind == null) return;
            if (SelectedEdge != null && _session.Current != null)
            {
                var edge = _session.Current.Edges.FirstOrDefault(e => e.Id == SelectedEdge.Id);
                if (edge != null && kind == MindmapEdgeKind.Hierarchy
                    && _mutator.WouldCreateCycle(edge.FromId, edge.ToId))
                    return;

                SelectedEdge.Kind = kind.Value;
                if (edge != null)
                {
                    edge.Kind = kind.Value;
                    await _mindmapService.UpdateEdgeKindAsync(_session.Current.Id, edge.Id, kind.Value);
                }

                OnPropertyChanged(nameof(EffectiveEdgeKind));
                OnPropertyChanged(nameof(StyleEdgeKindSelected));
            }
            else
            {
                DefaultEdgeKind = kind.Value;
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to set edge kind", ex);
        }
    }

    private async void SetSelectedEdgeType(string? type)
    {
        try
        {
            if (string.IsNullOrEmpty(type) || !EdgeTypeIds.Contains(type)) return;
            if (SelectedEdge != null && _session.Current != null)
            {
                await _mutator.SetEdgeTypeAsync(SelectedEdge, type);
                NotifyEffectiveEdgeTypeChanged();
            }
            else
            {
                DefaultEdgeType = type;
                NotifyEffectiveEdgeTypeChanged();
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to set edge type", ex);
        }
    }

    private async void SetSelectedNodesColor(string? color)
    {
        try
        {
            if (!HasSelectedNodes)
            {
                DefaultNodeColor = color;
                return;
            }

            if (_session.Current == null) return;
            foreach (var node in Nodes.Where(n => n.IsSelected))
            {
                node.Color = color;
                _session.SyncNodeStyleToModel(node);
                await _mindmapService.UpdateNodeStyleAsync(
                    _session.Current.Id, node.Id, MindmapEditorSession.BuildStyleDict(node));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to set node color", ex);
        }
    }

    private async void SetSelectedNodesShape(string? shape)
    {
        try
        {
            if (string.IsNullOrEmpty(shape)) return;
            if (!HasSelectedNodes)
            {
                DefaultNodeShape = shape;
                return;
            }

            if (_session.Current == null) return;
            foreach (var node in Nodes.Where(n => n.IsSelected))
            {
                node.Shape = shape;
                node.Width = null;
                node.Height = null;
                _session.SyncNodeStyleToModel(node);
                await _mindmapService.UpdateNodeStyleAsync(
                    _session.Current.Id, node.Id, MindmapEditorSession.BuildStyleDict(node));
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to set node shape", ex);
        }
    }
}
