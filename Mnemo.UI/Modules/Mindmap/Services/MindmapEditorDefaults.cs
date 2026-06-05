using Mnemo.Core.Models.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>Style defaults applied when creating nodes and edges.</summary>
public readonly record struct MindmapEditorDefaults(
    string? DefaultNodeColor,
    string DefaultNodeShape,
    MindmapEdgeKind DefaultEdgeKind,
    string DefaultEdgeType);
