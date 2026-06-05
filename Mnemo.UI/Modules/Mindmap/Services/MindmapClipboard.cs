using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.ViewModels;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

public sealed class MindmapClipboard
{
    public sealed class CopiedNodeData
    {
        public string OriginalId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public string? Color { get; init; }
        public string Shape { get; init; } = "pill";
        public double OffsetX { get; init; }
        public double OffsetY { get; init; }
    }

    public sealed class CopiedEdgeData
    {
        public string FromId { get; init; } = string.Empty;
        public string ToId { get; init; } = string.Empty;
        public MindmapEdgeKind Kind { get; init; }
        public string? Label { get; init; }
        public string Type { get; init; } = EdgeTypes.Solid;
    }

    private List<CopiedNodeData>? _nodes;
    private List<CopiedEdgeData>? _edges;

    public bool HasContent => _nodes is { Count: > 0 };

    public void Capture(MindmapModel mindmap, IReadOnlyList<NodeViewModel> selected)
    {
        if (selected.Count == 0)
        {
            _nodes = null;
            _edges = null;
            return;
        }

        var origin = selected[0];
        double originX = origin.X;
        double originY = origin.Y;

        _nodes = selected.Select(node => new CopiedNodeData
        {
            OriginalId = node.Id,
            Text = node.Text,
            Color = node.Color,
            Shape = node.Shape,
            OffsetX = node.X - originX,
            OffsetY = node.Y - originY
        }).ToList();

        var selectedIds = new HashSet<string>(selected.Select(n => n.Id));
        _edges = mindmap.Edges
            .Where(e => selectedIds.Contains(e.FromId) && selectedIds.Contains(e.ToId))
            .Select(e => new CopiedEdgeData
            {
                FromId = e.FromId,
                ToId = e.ToId,
                Kind = e.Kind,
                Label = e.Label,
                Type = string.IsNullOrWhiteSpace(e.Type) ? EdgeTypes.Solid : e.Type
            })
            .ToList();
    }

    public IReadOnlyList<CopiedNodeData>? Nodes => _nodes;
    public IReadOnlyList<CopiedEdgeData>? Edges => _edges;

    public void ReplaceNodes(IReadOnlyList<CopiedNodeData> nodes) => _nodes = nodes.ToList();
}
