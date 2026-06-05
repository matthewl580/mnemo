using Mnemo.Core.History;
using Mnemo.Core.Models.Mindmap;
using Mnemo.UI.Modules.Mindmap.Operations;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>Undo/redo snapshots for the mindmap editor.</summary>
public sealed class MindmapEditorHistory
{
    private readonly IHistoryManager _historyManager;
    private Func<MindmapModel, Task>? _restore;

    public MindmapEditorHistory(IHistoryManager historyManager)
    {
        _historyManager = historyManager;
        _historyManager.StateChanged += () => StateChanged?.Invoke();
    }

    public event Action? StateChanged;

    public bool CanUndo => _historyManager.CanUndo;
    public bool CanRedo => _historyManager.CanRedo;

    public void ConfigureRestore(Func<MindmapModel, Task> restore) => _restore = restore;

    public MindmapModel Snapshot(MindmapModel source) => MindmapCloner.Clone(source);

    public void Push(string description, MindmapModel before, MindmapModel after)
    {
        if (_restore == null)
            throw new InvalidOperationException("Restore handler not configured.");
        _historyManager.Push(new MindmapStateOperation(description, before, after, _restore));
    }

    public Task UndoAsync() => _historyManager.UndoAsync();
    public Task RedoAsync() => _historyManager.RedoAsync();
    public void Clear() => _historyManager.Clear();
}
