using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia.Platform;

namespace Mnemo.UI.Services;

/// <summary>
/// PDF pages are always light; inline <c>swatch1</c>…<c>swatch10</c> tokens use the Dawn palette
/// so Dusk/Noon editor themes do not paint dark swatches on white paper.
/// </summary>
internal static class PdfExportDawnSwatchResolver
{
    private static IReadOnlyDictionary<string, string>? _backgroundCache;
    private static IReadOnlyDictionary<string, string>? _foregroundCache;

    /// <summary>Fallback if <c>Colors.axaml</c> cannot be read; must stay in sync with Dawn background swatches.</summary>
    private static readonly IReadOnlyDictionary<string, string> BackgroundFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["swatch1"] = "#F5F5F5",
        ["swatch2"] = "#E6E6FA",
        ["swatch3"] = "#D8DCEC",
        ["swatch4"] = "#C4B5FD",
        ["swatch5"] = "#FADBD8",
        ["swatch6"] = "#E8F5E9",
        ["swatch7"] = "#FFF3CD",
        ["swatch8"] = "#FFE0B2",
        ["swatch9"] = "#DBEAFE",
        ["swatch10"] = "#D1EDDA"
    };

    /// <summary>Fallback if <c>Colors.axaml</c> cannot be read; must stay in sync with Dawn text swatches.</summary>
    private static readonly IReadOnlyDictionary<string, string> ForegroundFallback = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["swatch1"] = "#57534E",
        ["swatch2"] = "#7C3AED",
        ["swatch3"] = "#2563EB",
        ["swatch4"] = "#9333EA",
        ["swatch5"] = "#DC2626",
        ["swatch6"] = "#16A34A",
        ["swatch7"] = "#CA8A04",
        ["swatch8"] = "#EA580C",
        ["swatch9"] = "#0284C7",
        ["swatch10"] = "#0D9488"
    };

    public static IReadOnlyDictionary<string, string> GetBackgroundSwatchHexByName()
        => _backgroundCache ??= LoadSwatches("ColorSwatch", BackgroundFallback);

    public static IReadOnlyDictionary<string, string> GetForegroundSwatchHexByName()
        => _foregroundCache ??= LoadSwatches("TextColorSwatch", ForegroundFallback);

    private static IReadOnlyDictionary<string, string> LoadSwatches(
        string resourcePrefix,
        IReadOnlyDictionary<string, string> fallback)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var uri = new Uri("avares://Mnemo.UI/Themes/Core/Dawn/Colors.axaml");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var xml = reader.ReadToEnd();
            var pattern = "<Color x:Key=\"" + resourcePrefix + "(\\d{1,2})\">#([0-9A-Fa-f]{6})</Color>";
            foreach (Match m in Regex.Matches(xml, pattern, RegexOptions.IgnoreCase))
            {
                map["swatch" + m.Groups[1].Value] = "#" + m.Groups[2].Value.ToUpperInvariant();
            }
        }
        catch
        {
            // ignored
        }

        return map.Count >= 10 ? map : new Dictionary<string, string>(fallback, StringComparer.OrdinalIgnoreCase);
    }
}
