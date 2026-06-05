using Mnemo.UI.Modules.Mindmap.ViewModels;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>Tracks hover state for edge label highlighting in the canvas view.</summary>
public sealed class MindmapEdgeHoverState
{
    private readonly MindmapEditorSession _session;
    private string? _hoveredEdgeId;
    private readonly HashSet<string> _hoveredNodeIds = new();

    public MindmapEdgeHoverState(MindmapEditorSession session) => _session = session;

    public void SetHoveredEdge(string? edgeId)
    {
        if (_hoveredEdgeId == edgeId) return;
        var previousEdgeId = _hoveredEdgeId;
        _hoveredEdgeId = edgeId;
        UpdateEdgeHighlightingForEdges(previousEdgeId, edgeId);
    }

    public void SetHoveredNode(string nodeId, bool hovered)
    {
        if (hovered)
            _hoveredNodeIds.Add(nodeId);
        else
            _hoveredNodeIds.Remove(nodeId);
        UpdateEdgeHighlightingForNode(nodeId);
    }

    public void Clear()
    {
        if (_hoveredEdgeId == null && _hoveredNodeIds.Count == 0) return;
        var prevEdgeId = _hoveredEdgeId;
        var nodeIds = _hoveredNodeIds.ToList();
        _hoveredEdgeId = null;
        _hoveredNodeIds.Clear();
        UpdateEdgeHighlightingForEdges(prevEdgeId, null);
        foreach (var nid in nodeIds)
            UpdateEdgeHighlightingForNode(nid);
    }

    private void UpdateEdgeHighlightingForEdges(string? edgeId1, string? edgeId2)
    {
        foreach (var id in new[] { edgeId1, edgeId2 }.Where(x => x != null).Distinct())
        {
            var edge = _session.Edges.FirstOrDefault(e => e.Id == id);
            if (edge != null)
                ApplyHighlight(edge);
        }
    }

    private void UpdateEdgeHighlightingForNode(string nodeId)
    {
        var edgesToUpdate = new List<EdgeViewModel>();
        if (_session.Outgoing.TryGetValue(nodeId, out var outEdges)) edgesToUpdate.AddRange(outEdges);
        if (_session.Incoming.TryGetValue(nodeId, out var inEdges)) edgesToUpdate.AddRange(inEdges);
        foreach (var edge in edgesToUpdate.Distinct())
            ApplyHighlight(edge);
    }

    private void ApplyHighlight(EdgeViewModel edge) =>
        edge.IsLabelHighlighted = edge.Id == _hoveredEdgeId
            || _hoveredNodeIds.Contains(edge.From.Id)
            || _hoveredNodeIds.Contains(edge.To.Id);
}
