using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Operations;

/// <summary>UI-layer alias for <see cref="Mnemo.Core.Models.Mindmap.MindmapCloner"/> (undo/redo snapshots).</summary>
public static class MindmapSnapshotHelper
{
    public static MindmapModel Clone(MindmapModel source) =>
        Mnemo.Core.Models.Mindmap.MindmapCloner.Clone(source);
}
