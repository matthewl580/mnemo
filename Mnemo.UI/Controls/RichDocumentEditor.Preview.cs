using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.History;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Services.Notes.Markdown;
using Mnemo.UI.Components.BlockEditor;
using Mnemo.UI.Components.Overlays;


namespace Mnemo.UI.Controls;
public partial class RichDocumentEditor
{
    // â”€â”€ Preview â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void UpdatePreviewMarkdown()
    {
        PreviewMarkdown = InlineMarkdownSerializer.SerializeSpans(Spans);
        RaisePropertyChanged(nameof(PreviewMarkdown));
    }

    // â”€â”€ Mode toggle â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void UpdateModeButtonClasses()
    {
        if (WriteModeButton == null || PreviewModeButton == null || Editor == null || PreviewHost == null)
            return;
        SetSelectedClass(WriteModeButton, !IsPreviewMode);
        SetSelectedClass(PreviewModeButton, IsPreviewMode);
        Editor.IsVisible = !IsPreviewMode;
        Editor.IsHitTestVisible = !IsPreviewMode;
        Editor.IsSpellcheckDecorationsEnabled = !IsPreviewMode;
        PreviewHost.IsVisible = IsPreviewMode;
        PreviewHost.IsHitTestVisible = IsPreviewMode;
    }

    private static void SetSelectedClass(Button button, bool selected)
    {
        var has = button.Classes.Contains("selected");
        if (selected && !has)
            button.Classes.Add("selected");
        else if (!selected && has)
            button.Classes.Remove("selected");
    }

    // â”€â”€ Misc helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private string? ResolveHighlightColor()
    {
        if (Application.Current?.TryFindResource("InlineHighlightColor", out var resource) == true
            && resource is Avalonia.Media.Color color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        return null;
    }

    private string T(string key, string ns) => _localization?.T(key, ns) ?? key;

    private void RaisePropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

}
