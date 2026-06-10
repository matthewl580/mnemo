using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Linq;
using Mnemo.Core.Models;
using Mnemo.UI.Services;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Manages the block collection, row view-models, realized-block registry, and VM event subscriptions.
/// </summary>
internal sealed class BlockCollectionManager
{
    private readonly BlockEditor _host;

    internal BlockCollectionManager(BlockEditor host) => _host = host;

    internal void RegisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb)
    {
        _host._realizedBlocksByVm[vm] = eb;
    }

    internal void UnregisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb)
    {
        if (_host._realizedBlocksByVm.TryGetValue(vm, out var existing) && ReferenceEquals(existing, eb))
            _host._realizedBlocksByVm.Remove(vm);
    }

    /// <summary>Diagnostics-only: number of <see cref="EditableBlock"/> rows currently realized by the inner <see cref="ItemsRepeater"/>.</summary>
    public int RealizedRowCount => _host._realizedBlocksByVm.Count;

    /// <summary>
    /// Cheap structural hash of the live document (no serialization, no allocations beyond the
    /// enumerator). Autosave compares this against the last-saved value to skip the JSON-serialize +
    /// SQLite write entirely when nothing actually changed since the previous save.
    /// </summary>
    public long ComputeContentFingerprint()
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        var blockCount = 0;

        unchecked
        {
            long h = 1469598103934665603L; // FNV-1a 64-bit offset basis
            foreach (var b in BlockHierarchy.EnumerateInDocumentOrder(_host._blocks))
            {
                blockCount++;
                h = MixBlockFingerprint(h, b);
            }

            if (perfStart != 0)
            {
                EditorPerfDiagnostics.RecordIfSlow(
                    perf,
                    "computeContentFingerprint",
                    EditorPerfDiagnostics.ElapsedMs(perfStart),
                    $"blocks={blockCount} top={_host.Blocks.Count} realized={RealizedRowCount}");
            }

            return h;
        }
    }

    private static long MixBlockFingerprint(long h, BlockViewModel b)
    {
        unchecked
        {
            const long P = 1099511628211L; // FNV-1a 64-bit prime
            h = (h ^ (long)(b.Id?.GetHashCode() ?? 0)) * P;
            h = (h ^ (int)b.Type) * P;
            h = (h ^ b.Order) * P;
            h = (h ^ (long)(b.Content?.GetHashCode() ?? 0)) * P;

            foreach (var s in b.Spans)
                h = (h ^ (long)s.GetHashCode()) * P;

            switch (b.Type)
            {
                case BlockType.Checklist:
                    h = (h ^ (b.IsChecked ? 1L : 0L)) * P;
                    break;
                case BlockType.Image:
                    h = (h ^ (long)(b.ImagePath?.GetHashCode() ?? 0)) * P;
                    h = (h ^ (long)b.ImageWidth.GetHashCode()) * P;
                    h = (h ^ (long)(b.ImageAlign?.GetHashCode() ?? 0)) * P;
                    break;
                case BlockType.Sketch:
                    h = (h ^ (long)b.SketchWidth.GetHashCode()) * P;
                    h = (h ^ (long)(b.SketchAlign?.GetHashCode() ?? 0)) * P;
                    break;
                case BlockType.Code:
                    h = (h ^ (long)(b.CodeLanguage?.GetHashCode() ?? 0)) * P;
                    break;
                case BlockType.Equation:
                    h = (h ^ (long)(b.EquationLatex?.GetHashCode() ?? 0)) * P;
                    break;
                case BlockType.NumberedList:
                    h = (h ^ b.ListNumberIndex) * P;
                    break;
                case BlockType.Page:
                    h = (h ^ (long)(b.ReferenceNoteId?.GetHashCode() ?? 0)) * P;
                    break;
            }

            return h;
        }
    }

    public ObservableCollection<BlockViewModel> Blocks
    {
        get => _host._blocks;
        set
        {
            if (ReferenceEquals(_host._blocks, value)) return;
            _host._blocks.CollectionChanged -= OnBlocksCollectionChanged;
            _host._blocks = value ?? new ObservableCollection<BlockViewModel>();
            _host._blocks.CollectionChanged += OnBlocksCollectionChanged;
            _host._documentOrderDirty = true;
            _host._cachedDocumentOrder = null;
            _host._cachedDocIndexByVm = null;
            _host.NotifyBlocksPropertyChanged();
            RebuildBlockRows();
        }
    }

    internal void OnBlocksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _host._documentOrderDirty = true;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    var block = (BlockViewModel)e.NewItems[i]!;
                    var index = e.NewStartingIndex + i;
                    _host.BlockRows.Insert(index, MakeRow(block, index));
                    for (var j = index + 1; j < _host.BlockRows.Count; j++)
                        _host.BlockRows[j].StartBlockIndex = j;
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++)
                    _host.BlockRows.RemoveAt(e.OldStartingIndex);
                for (var j = e.OldStartingIndex; j < _host.BlockRows.Count; j++)
                    _host.BlockRows[j].StartBlockIndex = j;
                break;

            case NotifyCollectionChangedAction.Move:
                _host.BlockRows.Move(e.OldStartingIndex, e.NewStartingIndex);
                var lo = Math.Min(e.OldStartingIndex, e.NewStartingIndex);
                var hi = Math.Max(e.OldStartingIndex, e.NewStartingIndex);
                for (var j = lo; j <= hi; j++)
                    _host.BlockRows[j].StartBlockIndex = j;
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildBlockRows();
                break;

            default:
                RebuildBlockRows();
                break;
        }
    }

    internal void RebuildBlockRows()
    {
        _host.ClearBlockRowHeightCache();
        if (Blocks.Count == 0)
        {
            _host._blockRows = new ObservableCollection<BlockRowViewModelBase>();
            _host.NotifyBlockRowsChanged();
            return;
        }

        var rows = new BlockRowViewModelBase[Blocks.Count];
        for (var i = 0; i < Blocks.Count; i++)
            rows[i] = MakeRow(Blocks[i], i);
        _host._blockRows = new ObservableCollection<BlockRowViewModelBase>(rows);
        _host.RefreshAllRowLayoutHeightHints();
        _host.NotifyBlockRowsChanged();
        _host.InvalidateBlockListMeasure();
    }

    private static BlockRowViewModelBase MakeRow(BlockViewModel block, int index) =>
        block is TwoColumnBlockViewModel tc
            ? new SplitBlockRowViewModel(tc, index)
            : new SingleBlockRowViewModel(block, index);

    internal void SubscribeToBlock(BlockViewModel block)
    {
        if (block.Type == BlockType.NumberedList)
            _host._numberedListBlocks.Add(block);

        block.ContentChanged += _host.Lifecycle.OnBlockContentChanged;
        block.PropertyChanged += _host.Lifecycle.OnBlockPropertyChanged;
        block.DeleteRequested += _host.Lifecycle.OnBlockDeleteRequested;
        block.DuplicateBlockRequested += _host.OnDuplicateBlockRequested;
        block.NewBlockRequested += _host.OnNewBlockRequested;
        block.NewBlockOfTypeRequested += _host.OnNewBlockOfTypeRequested;
        block.NewBlockAboveRequested += _host.OnNewBlockAboveRequested;
        block.DeleteAndFocusAboveRequested += _host.Lifecycle.OnDeleteAndFocusAboveRequested;
        block.FocusPreviousRequested += _host.OnFocusPreviousRequested;
        block.FocusNextRequested += _host.OnFocusNextRequested;
        block.MergeWithPreviousRequested += _host.Lifecycle.OnMergeWithPreviousRequested;
        block.ExitSplitBelowRequested += _host.Lifecycle.OnExitSplitBelowRequested;
        block.StructuralChangeStarting += _host.Lifecycle.OnStructuralChangeStarting;
        block.StructuralChangeCompleted += _host.Lifecycle.OnStructuralChangeCompleted;
        if (block is TwoColumnBlockViewModel tc)
        {
            tc.ColumnChildrenChanged += OnTwoColumnColumnChildrenChanged;
            foreach (var c in tc.LeftColumnBlocks)
            {
                BlockHierarchy.WireChildOwnership(tc, c, true);
                SubscribeToBlock(c);
            }
            foreach (var c in tc.RightColumnBlocks)
            {
                BlockHierarchy.WireChildOwnership(tc, c, false);
                SubscribeToBlock(c);
            }
        }
    }

    internal void OnTwoColumnColumnChildrenChanged(TwoColumnBlockViewModel tc, bool leftColumn, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (BlockViewModel n in e.NewItems)
            {
                BlockHierarchy.WireChildOwnership(tc, n, leftColumn);
                SubscribeToBlock(n);
            }
        }
        if (e.OldItems != null)
        {
            foreach (BlockViewModel o in e.OldItems)
                UnsubscribeFromBlock(o, registerReleasedStoredImagePath: false);
        }
        _host.UpdateListNumbers();
        _host.NotifyBlocksChanged();
    }

    internal void UnsubscribeFromBlock(BlockViewModel block, bool registerReleasedStoredImagePath = false)
    {
        if (registerReleasedStoredImagePath && block.Type == BlockType.Image)
            _host.RegisterReleasedStoredImagePathCore(block.ImagePath);

        _host._numberedListBlocks.Remove(block);

        block.ContentChanged -= _host.Lifecycle.OnBlockContentChanged;
        block.PropertyChanged -= _host.Lifecycle.OnBlockPropertyChanged;
        block.DeleteRequested -= _host.Lifecycle.OnBlockDeleteRequested;
        block.DuplicateBlockRequested -= _host.OnDuplicateBlockRequested;
        block.NewBlockRequested -= _host.OnNewBlockRequested;
        block.NewBlockOfTypeRequested -= _host.OnNewBlockOfTypeRequested;
        block.NewBlockAboveRequested -= _host.OnNewBlockAboveRequested;
        block.DeleteAndFocusAboveRequested -= _host.Lifecycle.OnDeleteAndFocusAboveRequested;
        block.FocusPreviousRequested -= _host.OnFocusPreviousRequested;
        block.FocusNextRequested -= _host.OnFocusNextRequested;
        block.MergeWithPreviousRequested -= _host.Lifecycle.OnMergeWithPreviousRequested;
        block.ExitSplitBelowRequested -= _host.Lifecycle.OnExitSplitBelowRequested;
        block.StructuralChangeStarting -= _host.Lifecycle.OnStructuralChangeStarting;
        block.StructuralChangeCompleted -= _host.Lifecycle.OnStructuralChangeCompleted;
        if (block is TwoColumnBlockViewModel tc)
        {
            tc.ColumnChildrenChanged -= OnTwoColumnColumnChildrenChanged;
            foreach (var c in tc.LeftColumnBlocks.ToList())
                UnsubscribeFromBlock(c, registerReleasedStoredImagePath);
            foreach (var c in tc.RightColumnBlocks.ToList())
                UnsubscribeFromBlock(c, registerReleasedStoredImagePath);
        }
    }

}
