using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mnemo.UI.Components.BlockEditor.FormattingToolbar;

internal static class FormattingColorResolver
{
    internal static Color? TryResolveForeground(string? colorOrSwatch)
        => TryResolve(colorOrSwatch, "TextColorSwatch");

    internal static Color? TryResolveBackground(string? colorOrSwatch)
        => TryResolve(colorOrSwatch, "ColorSwatch");

    internal static IBrush? ToForegroundBrush(string? colorOrSwatch)
    {
        var color = TryResolveForeground(colorOrSwatch);
        return color.HasValue ? new SolidColorBrush(color.Value) : null;
    }

    internal static IBrush? ToBackgroundBrush(string? colorOrSwatch)
    {
        var color = TryResolveBackground(colorOrSwatch);
        return color.HasValue ? new SolidColorBrush(color.Value) : null;
    }

    private static Color? TryResolve(string? colorOrSwatch, string swatchResourcePrefix)
    {
        if (string.IsNullOrEmpty(colorOrSwatch))
            return null;

        if (Color.TryParse(colorOrSwatch, out var color))
            return color;

        if (colorOrSwatch.StartsWith("swatch", StringComparison.OrdinalIgnoreCase)
            && Application.Current != null)
        {
            var key = swatchResourcePrefix + colorOrSwatch.Substring(6);
            if (Application.Current.TryFindResource(key, out var res) && res is Color rc)
                return rc;
        }

        return null;
    }
}
