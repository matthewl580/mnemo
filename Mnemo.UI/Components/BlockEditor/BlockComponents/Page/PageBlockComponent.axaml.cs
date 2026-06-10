using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Services;
using EditorHost = Mnemo.UI.Components.BlockEditor.BlockEditor;

namespace Mnemo.UI.Components.BlockEditor.BlockComponents.Page;

public partial class PageBlockComponent : BlockComponentBase
{
    public PageBlockComponent()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    public override Control? GetInputControl() => null;

    private void OpenPageButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null || string.IsNullOrWhiteSpace(ViewModel.ReferenceNoteId))
            return;
        var editor = this.GetVisualAncestors().OfType<EditorHost>().FirstOrDefault();
        editor?.RequestOpenReferencedNote(ViewModel.ReferenceNoteId);
    }

    private void OpenPageButton_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Delete and not Key.Back || ViewModel == null)
            return;
        e.Handled = true;
        _ = ConfirmAndDeleteAsync();
    }

    private void DeleteButton_Click(object? sender, RoutedEventArgs e)
    {
        // Stop the click from bubbling up to the card's Click handler (which would open the page).
        e.Handled = true;
        if (ViewModel == null) return;
        _ = ConfirmAndDeleteAsync();
    }

    private async Task ConfirmAndDeleteAsync()
    {
        if (ViewModel == null) return;

        var overlay = (Application.Current as Mnemo.UI.App)?.Services?.GetService<IOverlayService>();
        if (overlay != null)
        {
            var title = ViewModel.ReferencedNoteTitle;
            var displayName = string.IsNullOrWhiteSpace(title) ? "this page" : $"\"{title}\"";
            var choice = await overlay.CreateDialogAsync(
                "Remove page block",
                $"Remove the link to {displayName}? The linked note itself will not be deleted.",
                "Remove",
                "Cancel");
            if (choice != "Remove")
                return;
        }

        ViewModel.NotifyStructuralChangeStarting();
        ViewModel.RequestDelete();
    }
}
