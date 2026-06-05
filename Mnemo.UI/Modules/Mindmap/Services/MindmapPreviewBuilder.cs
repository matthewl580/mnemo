using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.ViewModels;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>
/// Builds scaled node/edge previews for mindmap library cards.
/// </summary>
public static class MindmapPreviewBuilder
{
    private const double Padding = 20;
    private const double TargetWidth = 240;
    private const double TargetHeight = 120;

    public static void PopulatePreviews(MindmapItemViewModel item, MindmapModel mindmap)
    {
        item.NodePreviews.Clear();
        item.EdgePreviews.Clear();

        if (mindmap.Layout?.Nodes == null || mindmap.Layout.Nodes.Count == 0)
            return;

        var nodes = mindmap.Layout.Nodes.Values.ToList();
        double minX = nodes.Min(n => n.X);
        double maxX = nodes.Max(n => n.X);
        double minY = nodes.Min(n => n.Y);
        double maxY = nodes.Max(n => n.Y);

        double width = maxX - minX;
        double height = maxY - minY;

        double scaleX = width > 0 ? (TargetWidth - Padding * 2) / width : 1;
        double scaleY = height > 0 ? (TargetHeight - Padding * 2) / height : 1;
        double scale = Math.Min(scaleX, scaleY);

        foreach (var nodeEntry in mindmap.Layout.Nodes)
        {
            item.NodePreviews.Add(new NodePreviewViewModel
            {
                X = (nodeEntry.Value.X - minX) * scale + Padding,
                Y = (nodeEntry.Value.Y - minY) * scale + Padding
            });
        }

        foreach (var edge in mindmap.Edges)
        {
            if (mindmap.Layout.Nodes.TryGetValue(edge.FromId, out var source) &&
                mindmap.Layout.Nodes.TryGetValue(edge.ToId, out var target))
            {
                item.EdgePreviews.Add(new EdgePreviewViewModel
                {
                    X1 = (source.X - minX) * scale + Padding,
                    Y1 = (source.Y - minY) * scale + Padding,
                    X2 = (target.X - minX) * scale + Padding,
                    Y2 = (target.Y - minY) * scale + Padding
                });
            }
        }
    }
}
