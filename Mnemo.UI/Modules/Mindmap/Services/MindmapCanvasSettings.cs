using CommunityToolkit.Mvvm.ComponentModel;

namespace Mnemo.UI.Modules.Mindmap.Services;

/// <summary>Canvas grid and pointer-modifier settings loaded from <see cref="Mnemo.Core.Services.ISettingsService"/>.</summary>
public partial class MindmapCanvasSettings : ObservableObject
{
    [ObservableProperty]
    private string _gridType = "Dotted";

    [ObservableProperty]
    private double _gridSpacing = 40;

    [ObservableProperty]
    private double _gridDotSize = 1.5;

    [ObservableProperty]
    private double _gridOpacity = 0.2;

    /// <summary>Either <c>Selecting</c> or <c>Panning</c> (stored setting value).</summary>
    [ObservableProperty]
    private string _modifierBehaviour = "Selecting";
}
