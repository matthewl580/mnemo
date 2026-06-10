using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Services.LaTeX;
using Mnemo.UI;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Resolved service references for a single <see cref="RichTextEditor"/> instance.
/// All properties are optional; null means the service is unavailable (design-time, unit-test,
/// or the DI container has not registered it).
/// </summary>
internal sealed record RichTextServices(
    ISpellcheckService? Spellcheck = null,
    ITextShortcutService? Shortcuts = null,
    ILaTeXEngine? LaTeX = null,
    ISettingsService? Settings = null,
    ILocalizationService? Localization = null)
{
    /// <summary>
    /// Resolves all services from the running application's DI container.
    /// Returns an empty instance when no container is available (design-time / unit-test).
    /// </summary>
    internal static RichTextServices Resolve()
    {
        if ((Application.Current as App)?.Services is not { } sp)
            return new RichTextServices();

        return new RichTextServices(
            Spellcheck: sp.GetService<ISpellcheckService>(),
            Shortcuts: sp.GetService<ITextShortcutService>(),
            LaTeX: sp.GetService<ILaTeXEngine>(),
            Settings: sp.GetService<ISettingsService>(),
            Localization: sp.GetService<ILocalizationService>());
    }
}
