using CommunityToolkit.Mvvm.ComponentModel;

namespace Mnemo.UI.Modules.Notes.Services;

/// <summary>Editor and sidebar display settings loaded from <see cref="Mnemo.Core.Services.ISettingsService"/>.</summary>
public partial class NotesEditorSettings : ObservableObject
{
    [ObservableProperty]
    private double _editorMaxWidth = 1000;

    [ObservableProperty]
    private bool _isSidebarOpen;
}
