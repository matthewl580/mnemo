namespace Mnemo.Core.Models.Mindmap;

/// <summary>Creates copies of mindmaps for duplicate/export workflows.</summary>
public static class MindmapDuplicate
{
    public static Mindmap WithNewId(Mindmap source, string title)
    {
        var copy = MindmapCloner.Clone(source);
        copy.Id = Guid.NewGuid().ToString("n");
        copy.Title = title;
        return copy;
    }
}
