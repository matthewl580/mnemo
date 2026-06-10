using Mnemo.UI.Services.LaTeX.Layout.Boxes;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>One resolved inline equation: layout box, reserved pixel metrics, and source LaTeX.</summary>
internal sealed class InlineEquationEntry
{
    public int CharIndex;
    public string Latex = string.Empty;
    public Box? Layout;
    public double Width;
    public double Height;
}
