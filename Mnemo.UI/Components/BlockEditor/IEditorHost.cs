using System;
using System.Threading.Tasks;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>Optional host callbacks for note-specific editor features (page blocks, navigation).</summary>
public interface IEditorHost
{
    string? HostNoteId { get; }
    Func<string, string?>? NoteTitleResolver { get; }
    Func<string, int>? ChildPageCountResolver { get; }
    Func<string, Task<string?>>? CreateChildPageUnderNoteAsync { get; }
    event Action<string>? OpenReferencedNote;
}
