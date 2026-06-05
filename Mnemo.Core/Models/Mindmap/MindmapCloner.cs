using System.Text.Json;

namespace Mnemo.Core.Models.Mindmap;

/// <summary>
/// Deep-clones mindmap models for undo/redo and other snapshot workflows.
/// </summary>
public static class MindmapCloner
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static Mindmap Clone(Mindmap source)
    {
        var json = JsonSerializer.Serialize(source, s_options);
        return JsonSerializer.Deserialize<Mindmap>(json, s_options)!;
    }
}
