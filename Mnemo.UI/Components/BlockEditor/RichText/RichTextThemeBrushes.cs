using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Models;
using Mnemo.Core.Services;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Theme-aware brush and color resolvers for the rich-text editor. All methods fall back to
/// hardcoded defaults so they work in design-time and unit-test contexts without a live app.
/// </summary>
internal static class RichTextThemeBrushes
{
    internal static IBrush GetThemeForeground()
    {
        if (Application.Current == null)
            return new SolidColorBrush(Colors.Gray);
        try
        {
            var brush = Application.Current.FindResource("TextPrimaryBrush");
            return brush is IBrush b ? b : new SolidColorBrush(Colors.Gray);
        }
        catch
        {
            return new SolidColorBrush(Colors.Gray);
        }
    }

    internal static IBrush GetThemeSelectionBrush()
    {
        if (Application.Current?.TryFindResource("TextControlSelectionHighlightColorBrush", out var res) == true
            && res is IBrush brush)
            return brush;
        return new SolidColorBrush(Colors.CornflowerBlue, 0.4);
    }

    internal static IBrush GetThemeCaretBrush()
    {
        if (Application.Current?.TryFindResource("TextControlForegroundBrush", out var fgRes) == true
            && fgRes is IBrush fgBrush)
            return fgBrush;
        if (Application.Current?.TryFindResource("TextPrimaryBrush", out var primaryRes) == true
            && primaryRes is IBrush primaryBrush)
            return primaryBrush;
        return Brushes.Black;
    }

    internal static IBrush GetSearchHighlightBrush()
    {
        if (Application.Current?.TryFindResource("SearchMatchHighlightBrush", out var searchBrushResource) == true
            && searchBrushResource is IBrush searchBrush)
            return searchBrush;
        if (Application.Current?.TryFindResource("SearchMatchHighlight", out var searchColorResource) == true
            && searchColorResource is Color searchColor)
            return new SolidColorBrush(searchColor);
        if (Application.Current?.TryFindResource("HighlightedTextBrush", out var resource) == true
            && resource is IBrush brush)
            return brush;
        if (Application.Current?.TryFindResource("HighlightedText", out var colorResource) == true
            && colorResource is Color color)
            return new SolidColorBrush(color);
        return new SolidColorBrush(Color.FromArgb(0x8A, 0xFB, 0xDC, 0xAB));
    }

    internal static IBrush GetActiveSearchHighlightBrush() => GetSearchHighlightBrush();

    internal static IBrush GetInlineHighlightBrush()
    {
        if (Application.Current?.TryFindResource("InlineHighlightColorBrush", out var brushRes) == true
            && brushRes is IBrush brush)
            return brush;
        if (Application.Current?.TryFindResource("InlineHighlightColor", out var colorRes) == true
            && colorRes is Color color)
            return new SolidColorBrush(color);
        return new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0xAA));
    }

    internal static IBrush? ResolveInlineBackgroundBrush(TextStyle style)
    {
        if (style.Highlight)
            return GetInlineHighlightBrush();
        if (string.IsNullOrEmpty(style.BackgroundColor))
            return null;

        if (Color.TryParse(style.BackgroundColor, out var color))
            return new SolidColorBrush(color);

        if (style.BackgroundColor.StartsWith("swatch", StringComparison.OrdinalIgnoreCase)
            && Application.Current != null)
        {
            var key = "ColorSwatch" + style.BackgroundColor.Substring(6);
            if (Application.Current.TryFindResource(key, out var swatch))
            {
                if (swatch is Color swatchColor)
                    return new SolidColorBrush(swatchColor);
                if (swatch is IBrush swatchBrush)
                    return swatchBrush;
            }
        }

        return null;
    }

    internal static IBrush GetEquationActiveHighlightBrush()
    {
        if (Application.Current?.TryFindResource("OverlayHighlightColorBrush", out var r) == true && r is IBrush b)
            return b;
        return new SolidColorBrush(Color.FromArgb(0xCC, 0x46, 0x45, 0x49));
    }

    internal static string TNotes(string key)
    {
        var loc = (Application.Current as App)?.Services?.GetService<ILocalizationService>();
        return loc?.T(key, "NotesEditor") ?? key;
    }
}
