using Mnemo.Core.History;
using Mnemo.Core.Models;

namespace Mnemo.UI.Modules.Notes.Services;

/// <summary>Thin facade over <see cref="IHistoryManager"/> for the notes editor (block undo stays in BlockEditor).</summary>
public sealed class NotesEditorHistory
{
    private readonly IHistoryManager _historyManager;

    public NotesEditorHistory(IHistoryManager historyManager)
    {
        _historyManager = historyManager;
        _historyManager.StateChanged += () => StateChanged?.Invoke();
    }

    public event Action? StateChanged;

    public bool CanUndo => _historyManager.CanUndo;
    public bool CanRedo => _historyManager.CanRedo;

    public IHistoryManager Manager => _historyManager;

    public Task UndoAsync() => _historyManager.UndoAsync();
    public Task RedoAsync() => _historyManager.RedoAsync();
    public void Clear() => _historyManager.Clear();

    public void ClearOnNoteSwitch(Note? previous, Note? next)
    {
        if (previous?.NoteId != next?.NoteId)
            Clear();
    }
}
