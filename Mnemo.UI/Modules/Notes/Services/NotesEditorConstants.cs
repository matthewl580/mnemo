namespace Mnemo.UI.Modules.Notes.Services;

internal static class NotesEditorConstants
{
    public const string LastOpenNoteIdKey = "Notes.LastOpenNoteId";
    public const string NotesSidebarOpenKey = "Notes.SidebarOpen";
    public const string NotesExpandedFolderIdsKey = "Notes.ExpandedFolderIds";
    public const string EditorWidthKey = "Editor.Width";

    public const int AutosaveDebounceMs = 500;
    public const string NoteTreeItemDragKey = "NoteTreeItemViewModel";

    public const double MinEditorZoom = 0.5;
    public const double MaxEditorZoom = 10.0;
}
