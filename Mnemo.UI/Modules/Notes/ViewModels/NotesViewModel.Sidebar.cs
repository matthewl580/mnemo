using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mnemo.UI.Modules.Notes.Services;

namespace Mnemo.UI.Modules.Notes.ViewModels;

public partial class NotesViewModel
{
    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    private async Task UpdateEditorWidthAsync()
    {
        var widthStr = await _settingsService.GetAsync(NotesEditorConstants.EditorWidthKey, _localizationService.T("Wide", "Settings"));
        if (string.IsNullOrWhiteSpace(widthStr))
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => EditorMaxWidth = 1000);
            return;
        }

        var superCompact = _localizationService.T("SuperCompact", "Settings");
        var compact = _localizationService.T("Compact", "Settings");
        var wide = _localizationService.T("Wide", "Settings");
        var superWide = _localizationService.T("SuperWide", "Settings");

        double width = 1000;
        if (widthStr == superCompact) width = 600;
        else if (widthStr == compact) width = 800;
        else if (widthStr == wide) width = 1000;
        else if (widthStr == superWide) width = 1600;

        var w = width;
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => EditorMaxWidth = w);
    }
}
