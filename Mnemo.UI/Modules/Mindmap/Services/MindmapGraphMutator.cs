using Mnemo.Core.Models.Mindmap;
using Mnemo.Core.Services;
using Mnemo.UI.Modules.Mindmap.ViewModels;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>Graph mutations: add/remove nodes and edges, layout, clipboard, moves.</summary>
public sealed class MindmapGraphMutator
{
    private readonly IMindmapService _mindmapService;
    private readonly IMindmapLayoutService _layoutService;
    private readonly MindmapEditorSession _session;
    private readonly MindmapEditorHistory _history;
    private readonly MindmapClipboard _clipboard;
    private readonly ILoggerService? _logger;

    public MindmapGraphMutator(
        IMindmapService mindmapService,
        IMindmapLayoutService layoutService,
        MindmapEditorSession session,
        MindmapEditorHistory history,
        MindmapClipboard clipboard,
        ILoggerService? logger = null)
    {
        _mindmapService = mindmapService;
        _layoutService = layoutService;
        _session = session;
        _history = history;
        _clipboard = clipboard;
        _logger = logger;
    }

    public MindmapModel? CaptureMoveSnapshot() =>
        _session.Current == null ? null : _history.Snapshot(_session.Current);

    public async Task AddNodeAsync(MindmapEditorDefaults defaults, IReadOnlyList<NodeViewModel> selectedNodes)
    {
        if (_session.Current == null) return;

        var before = _history.Snapshot(_session.Current);
        double x = MindmapEditorConstants.DefaultSpawnX;
        double y = MindmapEditorConstants.DefaultSpawnY;
        if (selectedNodes.Any())
        {
            var last = selectedNodes.Last();
            x = last.X + MindmapEditorConstants.NewNodeXOffset;
            y = last.Y;
        }

        var result = await _mindmapService.AddNodeAsync(
            _session.Current.Id, "text", new TextNodeContent { Text = "New Node" }, x, y);

        if (!result.IsSuccess || result.Value == null) return;

        await ApplyDefaultStyleAsync(result.Value.Id, defaults);
        foreach (var selected in selectedNodes)
            await _mindmapService.AddEdgeAsync(
                _session.Current.Id, selected.Id, result.Value.Id,
                defaults.DefaultEdgeKind, null, defaults.DefaultEdgeType);

        await ReloadCurrentAsync();
        _history.Push("Add node", before, _history.Snapshot(_session.Current!));
    }

    public async Task AddChildNodeAsync(MindmapEditorDefaults defaults, NodeViewModel parent)
    {
        if (_session.Current == null) return;

        var before = _history.Snapshot(_session.Current);
        var result = await _mindmapService.AddNodeAsync(
            _session.Current.Id, "text", new TextNodeContent { Text = "New Node" },
            parent.X + MindmapEditorConstants.NewNodeXOffset, parent.Y);

        if (!result.IsSuccess || result.Value == null) return;

        await ApplyDefaultStyleAsync(result.Value.Id, defaults);
        await _mindmapService.AddEdgeAsync(
            _session.Current.Id, parent.Id, result.Value.Id,
            defaults.DefaultEdgeKind, null, defaults.DefaultEdgeType);

        await ReloadCurrentAsync();
        _history.Push("Add child node", before, _history.Snapshot(_session.Current!));
    }

    public async Task AddSiblingNodeAsync(MindmapEditorDefaults defaults, NodeViewModel node)
    {
        if (_session.Current == null) return;

        var parentEdge = _session.Current.Edges
            .FirstOrDefault(e => e.Kind == MindmapEdgeKind.Hierarchy && e.ToId == node.Id);

        var before = _history.Snapshot(_session.Current);
        double x;
        double y;
        string? parentId = null;

        if (parentEdge != null)
        {
            parentId = parentEdge.FromId;
            x = node.X;
            y = node.Y + MindmapEditorConstants.SiblingNodeVSpacing;
        }
        else
        {
            x = node.X + MindmapEditorConstants.NewNodeXOffset;
            y = node.Y;
        }

        var result = await _mindmapService.AddNodeAsync(
            _session.Current.Id, "text", new TextNodeContent { Text = "New Node" }, x, y);

        if (!result.IsSuccess || result.Value == null) return;

        await ApplyDefaultStyleAsync(result.Value.Id, defaults);
        if (parentId != null)
            await _mindmapService.AddEdgeAsync(
                _session.Current.Id, parentId, result.Value.Id,
                defaults.DefaultEdgeKind, null, defaults.DefaultEdgeType);

        await ReloadCurrentAsync();
        _history.Push("Add sibling node", before, _history.Snapshot(_session.Current!));
    }

    public async Task ConnectSelectedAsync(MindmapEditorDefaults defaults, IReadOnlyList<NodeViewModel> selected)
    {
        if (_session.Current == null || selected.Count < 2) return;

        var before = _history.Snapshot(_session.Current);
        var from = selected[0];
        bool changed = false;
        for (int i = 1; i < selected.Count; i++)
        {
            var to = selected[i];
            if (!_session.Current.Edges.Any(e =>
                    (e.FromId == from.Id && e.ToId == to.Id) || (e.FromId == to.Id && e.ToId == from.Id)))
            {
                await _mindmapService.AddEdgeAsync(
                    _session.Current.Id, from.Id, to.Id,
                    defaults.DefaultEdgeKind, null, defaults.DefaultEdgeType);
                changed = true;
            }
        }

        if (!changed) return;
        await ReloadCurrentAsync();
        _history.Push("Connect nodes", before, _history.Snapshot(_session.Current!));
    }

    public async Task DetachSelectedAsync(IReadOnlyList<NodeViewModel> selected)
    {
        if (_session.Current == null || selected.Count == 0) return;

        var selectedIds = selected.Select(n => n.Id).ToHashSet();
        List<MindmapEdge> edgesToRemove = selectedIds.Count == 1
            ? _session.Current.Edges.Where(e => e.FromId == selectedIds.First() || e.ToId == selectedIds.First()).ToList()
            : _session.Current.Edges.Where(e => selectedIds.Contains(e.FromId) && selectedIds.Contains(e.ToId)).ToList();

        if (edgesToRemove.Count == 0) return;

        var before = _history.Snapshot(_session.Current);
        foreach (var edge in edgesToRemove)
            await _mindmapService.RemoveEdgeAsync(_session.Current.Id, edge.Id);

        await ReloadCurrentAsync();
        _history.Push("Detach edges", before, _history.Snapshot(_session.Current!));
    }

    public async Task DeleteSelectedAsync(IReadOnlyList<NodeViewModel> selected)
    {
        if (_session.Current == null || selected.Count == 0) return;

        var before = _history.Snapshot(_session.Current);
        foreach (var node in selected)
            await _mindmapService.RemoveNodeAsync(_session.Current.Id, node.Id);

        await ReloadCurrentAsync();
        _history.Push("Delete nodes", before, _history.Snapshot(_session.Current!));
    }

    public void CopySelection(IReadOnlyList<NodeViewModel> selected)
    {
        if (_session.Current == null) return;
        _clipboard.Capture(_session.Current, selected);
    }

    public async Task<IReadOnlyList<string>> PasteAsync(
        MindmapEditorDefaults defaults,
        NodeViewModel? target,
        NodeViewModel? fallbackTarget)
    {
        var anchor = target ?? fallbackTarget;
        if (_session.Current == null || anchor == null || !_clipboard.HasContent)
            return Array.Empty<string>();

        var before = _history.Snapshot(_session.Current);
        double baseX = anchor.X + MindmapEditorConstants.NewNodeXOffset;
        double baseY = anchor.Y;
        var idMap = new Dictionary<string, string>();
        var newIds = new List<string>();

        foreach (var copied in _clipboard.Nodes!)
        {
            var result = await _mindmapService.AddNodeAsync(
                _session.Current.Id, "text", new TextNodeContent { Text = copied.Text },
                baseX + copied.OffsetX, baseY + copied.OffsetY);

            if (!result.IsSuccess || result.Value == null) continue;

            idMap[copied.OriginalId] = result.Value.Id;
            newIds.Add(result.Value.Id);

            var style = new Dictionary<string, string?>();
            if (!string.IsNullOrWhiteSpace(copied.Color)) style["color"] = copied.Color;
            style["shape"] = string.IsNullOrWhiteSpace(copied.Shape) ? defaults.DefaultNodeShape : copied.Shape;
            if (style.Count > 0)
                await _mindmapService.UpdateNodeStyleAsync(_session.Current.Id, result.Value.Id, style);
        }

        if (_clipboard.Edges != null)
        {
            foreach (var edge in _clipboard.Edges)
            {
                if (!idMap.TryGetValue(edge.FromId, out var newFrom) || !idMap.TryGetValue(edge.ToId, out var newTo))
                    continue;
                await _mindmapService.AddEdgeAsync(
                    _session.Current.Id, newFrom, newTo, edge.Kind, edge.Label, edge.Type);
            }
        }

        await ReloadCurrentAsync();
        _history.Push("Paste nodes", before, _history.Snapshot(_session.Current!));
        return newIds;
    }

    public async Task DuplicateSelectionAsync(MindmapEditorDefaults defaults, IReadOnlyList<NodeViewModel> selected)
    {
        CopySelection(selected);
        if (!_clipboard.HasContent) return;

        var shifted = _clipboard.Nodes!
            .Select(c => new MindmapClipboard.CopiedNodeData
            {
                OriginalId = c.OriginalId,
                Text = c.Text,
                Color = c.Color,
                Shape = c.Shape,
                OffsetX = c.OffsetX + MindmapEditorConstants.NewNodeXOffset,
                OffsetY = c.OffsetY
            })
            .ToList();
        _clipboard.ReplaceNodes(shifted);
        await PasteAsync(defaults, selected.FirstOrDefault(), _session.Nodes.FirstOrDefault());
    }

    public async Task ToggleCollapseAsync(NodeViewModel node)
    {
        if (_session.Current == null) return;

        var before = _history.Snapshot(_session.Current);
        node.IsCollapsed = !node.IsCollapsed;
        _session.SyncNodeStyleToModel(node);
        _session.ComputeNodeVisibility();
        var after = _history.Snapshot(_session.Current);
        _history.Push(node.IsCollapsed ? "Collapse node" : "Expand node", before, after);
        await _mindmapService.UpdateNodeStyleAsync(
            _session.Current.Id, node.Id, MindmapEditorSession.BuildStyleDict(node));
    }

    public async Task UpdateNodeTextAsync(NodeViewModel node, string text)
    {
        if (_session.Current == null) return;

        var before = _history.Snapshot(_session.Current);
        var mn = _session.Current.Nodes.FirstOrDefault(n => n.Id == node.Id);
        if (mn != null)
        {
            if (mn.Content is TextNodeContent existingContent)
                existingContent.Text = text;
            else
                mn.Content = new TextNodeContent { Text = text };
        }

        var after = _history.Snapshot(_session.Current);
        _history.Push("Edit node", before, after);
        node.Text = text;
        await _mindmapService.UpdateNodeContentAsync(_session.Current.Id, node.Id, new TextNodeContent { Text = text });
    }

    public async Task UpdateNodesPositionAsync(
        MindmapModel before,
        IReadOnlyList<(NodeViewModel node, double x, double y)> moves)
    {
        if (_session.Current == null || moves.Count == 0) return;

        foreach (var (node, x, y) in moves)
        {
            if (!_session.Current.Layout.Nodes.TryGetValue(node.Id, out var layout))
            {
                layout = new NodeLayout();
                _session.Current.Layout.Nodes[node.Id] = layout;
            }

            layout.X = x;
            layout.Y = y;
            node.X = x;
            node.Y = y;
        }

        var after = _history.Snapshot(_session.Current);
        _history.Push("Move node", before, after);
        foreach (var (node, x, y) in moves)
            await _mindmapService.UpdateNodeLayoutAsync(_session.Current.Id, node.Id, x, y);
    }

    public async Task ApplyLayoutAsync()
    {
        if (_session.Current == null || _session.Nodes.Count == 0) return;

        var before = _history.Snapshot(_session.Current);
        _session.SyncLayoutMeasurementsFromView();
        _layoutService.Apply(_session.Current, _session.Current.Layout.Algorithm);
        _session.SyncNodePositionsFromModel();
        var after = _history.Snapshot(_session.Current);
        _history.Push("Auto layout", before, after);
        foreach (var node in _session.Nodes)
            await _mindmapService.UpdateNodeLayoutAsync(_session.Current.Id, node.Id, node.X, node.Y);
    }

    public async Task CommitEdgeLabelAsync(EdgeViewModel edge)
    {
        if (_session.Current == null) return;

        var modelEdge = _session.Current.Edges.FirstOrDefault(e => e.Id == edge.Id);
        if (modelEdge == null) return;

        var before = _history.Snapshot(_session.Current);
        if (string.IsNullOrWhiteSpace(edge.Label))
        {
            modelEdge.Label = null;
            edge.Label = null;
        }
        else
        {
            modelEdge.Label = edge.Label;
        }

        var after = _history.Snapshot(_session.Current);
        _history.Push("Edit edge label", before, after);
        await _mindmapService.UpdateEdgeLabelAsync(
            _session.Current.Id, edge.Id, string.IsNullOrWhiteSpace(edge.Label) ? null : edge.Label);
    }

    public async Task AddEdgeLabelAsync(EdgeViewModel edge)
    {
        if (_session.Current == null) return;

        var modelEdge = _session.Current.Edges.FirstOrDefault(e => e.Id == edge.Id);
        if (modelEdge == null || modelEdge.Label != null) return;

        var before = _history.Snapshot(_session.Current);
        modelEdge.Label = "";
        edge.Label = "";
        var after = _history.Snapshot(_session.Current);
        _history.Push("Add edge label", before, after);
        await _mindmapService.UpdateEdgeLabelAsync(_session.Current.Id, edge.Id, "");
    }

    public async Task SetEdgeTypeAsync(EdgeViewModel edgeVm, string type)
    {
        if (_session.Current == null) return;

        var edge = _session.Current.Edges.FirstOrDefault(e => e.Id == edgeVm.Id);
        if (edge == null) return;

        var before = _history.Snapshot(_session.Current);
        edgeVm.Type = type;
        edge.Type = type;
        var after = _history.Snapshot(_session.Current);
        _history.Push("Change edge type", before, after);
        await _mindmapService.UpdateEdgeTypeAsync(_session.Current.Id, edge.Id, type);
    }

    public bool WouldCreateCycle(string fromId, string toId) =>
        _session.Current != null && _mindmapService.WouldCreateCycle(_session.Current, fromId, toId);

    private async Task ApplyDefaultStyleAsync(string nodeId, MindmapEditorDefaults defaults)
    {
        if (_session.Current == null) return;
        var style = new Dictionary<string, string?>();
        if (defaults.DefaultNodeColor != null) style["color"] = defaults.DefaultNodeColor;
        style["shape"] = defaults.DefaultNodeShape;
        if (style.Count > 0)
            await _mindmapService.UpdateNodeStyleAsync(_session.Current.Id, nodeId, style);
    }

    private async Task ReloadCurrentAsync()
    {
        if (_session.Current == null) return;
        var result = await _mindmapService.GetMindmapAsync(_session.Current.Id);
        if (result.IsSuccess && result.Value != null)
            _session.Refresh(result.Value);
    }
}
