using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>One visual row in the block list (single block or a nested split row).</summary>
public abstract class BlockRowViewModelBase : INotifyPropertyChanged
{
    private double _layoutHeightHint;

    /// <summary>Index of the first <see cref="BlockViewModel"/> in <see cref="BlockEditor.Blocks"/> for this row.</summary>
    public int StartBlockIndex { get; set; }

    /// <summary>How many consecutive top-level <see cref="BlockEditor.Blocks"/> entries this row consumes (always 1 for a split; columns are nested).</summary>
    public int BlockSpan { get; protected init; }

    /// <summary>
    /// Estimated row height for ItemsRepeater virtualization. Tall rows (images) set a non-zero
    /// hint so virtualized-out slots are not measured as average text height.
    /// </summary>
    public double LayoutHeightHint
    {
        get => _layoutHeightHint;
        private set
        {
            if (Math.Abs(_layoutHeightHint - value) <= 0.5)
                return;
            _layoutHeightHint = value;
            OnPropertyChanged();
        }
    }

    internal void SetLayoutHeightHint(double height)
    {
        LayoutHeightHint = height;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class SingleBlockRowViewModel : BlockRowViewModelBase
{
    public BlockViewModel Block { get; }

    public SingleBlockRowViewModel(BlockViewModel block, int startBlockIndex)
    {
        Block = block;
        StartBlockIndex = startBlockIndex;
        BlockSpan = 1;
    }
}

public sealed class SplitBlockRowViewModel : BlockRowViewModelBase
{
    public TwoColumnBlockViewModel TwoColumn { get; }

    public SplitBlockRowViewModel(TwoColumnBlockViewModel twoColumn, int startBlockIndex)
    {
        TwoColumn = twoColumn;
        StartBlockIndex = startBlockIndex;
        BlockSpan = 1;
    }
}
