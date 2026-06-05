using Mnemo.Core.Models.Mindmap;

namespace Mnemo.Core.Services;

/// <summary>
/// Computes hierarchical node positions for a mindmap model (shared by editor and AI tools).
/// </summary>
public interface IMindmapLayoutService
{
    /// <summary>
    /// Updates <see cref="Mindmap.Layout"/> node coordinates for the given algorithm.
    /// Uses persisted <see cref="NodeLayout.Width"/> / <see cref="NodeLayout.Height"/> when set.
    /// </summary>
    void Apply(Mindmap mindmap, string? algorithm = null);
}
