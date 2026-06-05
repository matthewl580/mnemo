using System.Collections.ObjectModel;
using System.ComponentModel;
using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.ViewModels;
using LayoutAlgorithms = Mnemo.Core.Models.Mindmap.LayoutAlgorithms;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>
/// In-memory graph state: model, node/edge view models, adjacency, and visibility.
/// </summary>
public sealed class MindmapEditorSession
{
    private readonly Dictionary<string, List<EdgeViewModel>> _outgoing = new();
    private readonly Dictionary<string, List<EdgeViewModel>> _incoming = new();
    private PropertyChangedEventHandler? _nodePropertyHandler;

    public MindmapModel? Current { get; private set; }
    public ObservableCollection<NodeViewModel> Nodes { get; } = new();
    public ObservableCollection<EdgeViewModel> Edges { get; } = new();

    public IReadOnlyDictionary<string, List<EdgeViewModel>> Outgoing => _outgoing;
    public IReadOnlyDictionary<string, List<EdgeViewModel>> Incoming => _incoming;

    public void SetNodePropertyHandler(PropertyChangedEventHandler handler) =>
        _nodePropertyHandler = handler;

    /// <summary>Rebuilds collections from the model. Returns the normalized layout algorithm id.</summary>
    public string Refresh(MindmapModel mindmap)
    {
        Current = mindmap;

        var algorithm = mindmap.Layout.Algorithm;
        if (string.IsNullOrEmpty(algorithm) || algorithm == "Freeform"
            || algorithm is not (LayoutAlgorithms.TreeVertical or LayoutAlgorithms.TreeHorizontal or LayoutAlgorithms.Radial))
        {
            algorithm = LayoutAlgorithms.TreeVertical;
            mindmap.Layout.Algorithm = algorithm;
        }

        foreach (var edge in Edges)
            edge.Dispose();

        Nodes.Clear();
        Edges.Clear();
        _outgoing.Clear();
        _incoming.Clear();

        var nodeMap = new Dictionary<string, NodeViewModel>();

        foreach (var node in mindmap.Nodes)
        {
            if (!mindmap.Layout.Nodes.TryGetValue(node.Id, out var layout))
                continue;

            var nodeVm = new NodeViewModel(node, layout);
            if (_nodePropertyHandler != null)
                nodeVm.PropertyChanged += _nodePropertyHandler;
            Nodes.Add(nodeVm);
            nodeMap[node.Id] = nodeVm;
        }

        foreach (var edge in mindmap.Edges)
        {
            if (!nodeMap.TryGetValue(edge.FromId, out var from) || !nodeMap.TryGetValue(edge.ToId, out var to))
                continue;

            var edgeVm = new EdgeViewModel(edge, from, to);
            Edges.Add(edgeVm);
            AddToAdjacency(edgeVm);
        }

        foreach (var node in Nodes)
            node.HasChildren = _outgoing.TryGetValue(node.Id, out var outEdges)
                && outEdges.Any(e => e.Kind == MindmapEdgeKind.Hierarchy);

        ComputeNodeVisibility();
        return algorithm;
    }

    public void SyncLayoutFromView()
    {
        if (Current == null) return;
        foreach (var n in Nodes)
        {
            if (!Current.Layout.Nodes.TryGetValue(n.Id, out var layout))
            {
                layout = new NodeLayout();
                Current.Layout.Nodes[n.Id] = layout;
            }

            layout.X = n.X;
            layout.Y = n.Y;
            layout.Width = n.Width;
            layout.Height = n.Height;
        }
    }

    public void SyncLayoutMeasurementsFromView()
    {
        if (Current == null) return;
        foreach (var n in Nodes)
        {
            if (!Current.Layout.Nodes.TryGetValue(n.Id, out var layout))
            {
                layout = new NodeLayout();
                Current.Layout.Nodes[n.Id] = layout;
            }

            layout.Width = n.Width ?? n.ActualWidth;
            layout.Height = n.Height ?? n.ActualHeight;
        }
    }

    public void SyncNodePositionsFromModel()
    {
        if (Current == null) return;
        foreach (var n in Nodes)
        {
            if (Current.Layout.Nodes.TryGetValue(n.Id, out var layout))
            {
                n.X = layout.X;
                n.Y = layout.Y;
            }
        }
    }

    public static Dictionary<string, string?> BuildStyleDict(NodeViewModel node)
    {
        return new Dictionary<string, string?>
        {
            ["color"] = node.Color,
            ["shape"] = node.Shape,
            ["collapsed"] = node.IsCollapsed ? "true" : null
        };
    }

    public void SyncNodeStyleToModel(NodeViewModel nodeVm)
    {
        var node = Current?.Nodes.FirstOrDefault(n => n.Id == nodeVm.Id);
        if (node == null) return;
        if (nodeVm.Color != null) node.Style["color"] = nodeVm.Color;
        else node.Style.Remove("color");
        node.Style["shape"] = nodeVm.Shape;
        if (nodeVm.IsCollapsed) node.Style["collapsed"] = "true";
        else node.Style.Remove("collapsed");
    }

    public void ComputeNodeVisibility()
    {
        var hiddenIds = new HashSet<string>();

        foreach (var node in Nodes)
        {
            if (!node.IsCollapsed) continue;
            CollectHiddenDescendants(node.Id, hiddenIds);
        }

        foreach (var node in Nodes)
            node.IsHidden = hiddenIds.Contains(node.Id);

        foreach (var edge in Edges)
            edge.IsHidden = hiddenIds.Contains(edge.From.Id) || hiddenIds.Contains(edge.To.Id);
    }

    private void CollectHiddenDescendants(string nodeId, HashSet<string> hiddenIds)
    {
        if (!_outgoing.TryGetValue(nodeId, out var children)) return;
        foreach (var edge in children.Where(e => e.Kind == MindmapEdgeKind.Hierarchy))
        {
            if (hiddenIds.Add(edge.To.Id))
                CollectHiddenDescendants(edge.To.Id, hiddenIds);
        }
    }

    private void AddToAdjacency(EdgeViewModel edge)
    {
        if (!_outgoing.ContainsKey(edge.From.Id)) _outgoing[edge.From.Id] = new List<EdgeViewModel>();
        if (!_incoming.ContainsKey(edge.To.Id)) _incoming[edge.To.Id] = new List<EdgeViewModel>();
        _outgoing[edge.From.Id].Add(edge);
        _incoming[edge.To.Id].Add(edge);
    }
}
