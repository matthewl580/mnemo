using Mnemo.Core.Models;
using Mnemo.UI.Modules.Notes.ViewModels;

namespace Mnemo.UI.Modules.Notes.Services;

/// <summary>Editor selection context and navigation helpers.</summary>
public sealed class NotesEditorSession
{
    private readonly NotesLibrarySession _library;

    public NotesEditorSession(NotesLibrarySession library)
    {
        _library = library;
    }

    public NoteTreeItemViewModel? LastSelectedNoteTreeItem { get; set; }
    public bool SuppressSelectedNoteClearOnTreeItemNull { get; set; }

    public Block[] GetBlocksForCurrentNote(Note? selectedNote)
    {
        if (selectedNote == null) return Array.Empty<Block>();

        if (selectedNote.Blocks != null && selectedNote.Blocks.Count > 0)
        {
            var ordered = selectedNote.Blocks.OrderBy(b => b.Order).ToArray();
            for (var i = 0; i < ordered.Length; i++)
                ordered[i].Order = i;
            return ordered;
        }

        return new[]
        {
            new Block
            {
                Id = Guid.NewGuid().ToString(),
                Type = BlockType.Text,
                Spans = new List<InlineSpan> { InlineSpan.Plain(selectedNote.Content ?? "") },
                Order = 0
            }
        };
    }

    public string? ResolveNoteTitleForPageBlock(string noteId)
    {
        if (string.IsNullOrEmpty(noteId)) return null;
        return _library.Notes.FirstOrDefault(n => n.NoteId == noteId)?.Title;
    }

    public int CountDirectChildPagesForNote(string noteId)
    {
        if (string.IsNullOrEmpty(noteId)) return 0;
        return _library.Notes.Count(n => string.Equals(n.ParentNoteId, noteId, StringComparison.Ordinal));
    }

    /// <summary>Resolves navigation target for a note id.</summary>
    public NoteNavigationResult ResolveNavigation(string noteId)
    {
        var note = _library.Notes.FirstOrDefault(n => n.NoteId == noteId);
        if (note == null)
            return NoteNavigationResult.None;

        var item = _library.AllNotesTreeItems.FirstOrDefault(i => i.Note?.NoteId == noteId)
            ?? NotesLibrarySession.FindTreeItemByNoteId(_library.RootTreeItems, noteId);
        if (item != null)
            return NoteNavigationResult.SelectTreeItem(item);

        return NoteNavigationResult.SelectOffTreeNote(note);
    }

    public TreeItemSelectionResult HandleTreeItemChanged(
        NoteTreeItemViewModel? value,
        NoteTreeItemViewModel? currentTreeItem)
    {
        if (value != null && value.IsFolder)
            return TreeItemSelectionResult.RevertToLast(LastSelectedNoteTreeItem);

        if (value == null && _library.IsRefreshingCollections)
            return TreeItemSelectionResult.Unchanged;

        if (value == null && SuppressSelectedNoteClearOnTreeItemNull)
            return TreeItemSelectionResult.Unchanged;

        if (value == null)
        {
            LastSelectedNoteTreeItem = null;
            return TreeItemSelectionResult.ClearNote();
        }

        if (value.Note != null)
        {
            LastSelectedNoteTreeItem = value;
            return TreeItemSelectionResult.SelectNote(value.Note);
        }

        return TreeItemSelectionResult.ClearNote();
    }
}

public readonly struct NoteNavigationResult
{
    public NoteTreeItemViewModel? TreeItem { get; init; }
    public Note? OffTreeNote { get; init; }
    public bool IsNotFound { get; init; }

    public static NoteNavigationResult None => new() { IsNotFound = true };
    public static NoteNavigationResult SelectTreeItem(NoteTreeItemViewModel item) => new() { TreeItem = item };
    public static NoteNavigationResult SelectOffTreeNote(Note note) => new() { OffTreeNote = note };
}

public readonly struct TreeItemSelectionResult
{
    public Note? Note { get; init; }
    public NoteTreeItemViewModel? RevertTreeItem { get; init; }
    public bool Clear { get; init; }
    public bool IsNoChange { get; init; }

    public static TreeItemSelectionResult Unchanged => new() { IsNoChange = true };
    public static TreeItemSelectionResult ClearNote() => new() { Clear = true };
    public static TreeItemSelectionResult SelectNote(Note note) => new() { Note = note };
    public static TreeItemSelectionResult RevertToLast(NoteTreeItemViewModel? item) => new() { RevertTreeItem = item };
}
