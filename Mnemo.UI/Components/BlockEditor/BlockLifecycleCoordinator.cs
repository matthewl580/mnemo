using Avalonia.Threading;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Mnemo.Core.Models;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Handles block lifecycle events: property changes, content changes, delete/merge/split, and two-column unwrap.
/// </summary>
internal sealed class BlockLifecycleCoordinator
{
    private readonly BlockEditor _host;

    internal BlockLifecycleCoordinator(BlockEditor host) => _host = host;

    internal void OnStructuralChangeStarting()
    {
        _host.BeginStructuralChange();
    }

    internal void OnStructuralChangeCompleted(string description)
    {
        _host.CommitStructuralChange(description);
    }

    internal void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is BlockViewModel layoutBlock)
            _host.OnBlockLayoutAffectingPropertyChanged(layoutBlock, e);

        if (e.PropertyName == nameof(BlockViewModel.Type))
        {
            if (sender is BlockViewModel typedBlock)
            {
                if (typedBlock.Type == BlockType.NumberedList)
                    _host._numberedListBlocks.Add(typedBlock);
                else
                    _host._numberedListBlocks.Remove(typedBlock);
            }

            _host.UpdateListNumbers();
            if (!_host._isRestoringFromHistory && _host._pendingSnapshot != null)
                _host.CommitStructuralChange("Change block type");
            return;
        }

        if (e.PropertyName == nameof(BlockViewModel.IsChecked))
        {
            if (!_host._isRestoringFromHistory && _host._pendingSnapshot != null)
                _host.CommitStructuralChange("Toggle checklist");
            return;
        }

        if (e.PropertyName == nameof(BlockViewModel.IsSelected))
        {
            if (sender is BlockViewModel sb)
            {
                if (sb.IsSelected) _host._selectedBlockCount++;
                else if (_host._selectedBlockCount > 0) _host._selectedBlockCount--;
            }
            return;
        }

        if (e.PropertyName != nameof(BlockViewModel.IsFocused)) return;
        if (sender is not BlockViewModel block) return;

        if (block.IsFocused)
        {
            var topIdx = BlockHierarchy.GetTopLevelIndex(_host.Blocks, block);
            if (!string.IsNullOrEmpty(_host._focusedBlockId) && _host._focusedBlockId != block.Id)
            {
                var prev = BlockHierarchy.FindById(_host.Blocks, _host._focusedBlockId);
                if (prev?.IsFocused == true)
                    prev.IsFocused = false;
            }

            _host._focusedBlockId = block.Id;
            _host._focusedBlockIndex = topIdx;

            if (_host._isCrossBlockSelecting || _host._crossBlockArmed) return;
            if (_host._isApplyingCrossBlockFormat) return;

            _host.ClearBlockSelection();
            _host.ClearTextSelectionInAllBlocksExcept(block);
        }
        else if (!string.IsNullOrEmpty(_host._focusedBlockId) && _host._focusedBlockId == block.Id)
        {
            _host._focusedBlockId = null;
            _host._focusedBlockIndex = -1;
        }
    }

    internal void OnBlockContentChanged(BlockViewModel block)
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        _host.ClearBlockSelection();
        var prev = block.PreviousContent;
        var prevRuns = block.PreviousSpans;
        block.PreviousContent = null;
        block.PreviousSpans = null;

        if (!_host._isRestoringFromHistory && _host._pendingSnapshot == null)
        {
            if (prev != null || prevRuns != null)
                _host.TrackTypingEdit(block, prev ?? block.Content, prevRuns);
        }

        if (_host._findPanelVisible)
        {
            var findStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
            _host.RefreshFindMatchesAndHighlights();
            EditorPerfDiagnostics.RecordPhase(perf, findStart, "contentChanged.findRefresh");
        }

        var notifyStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        _host.RaiseBlocksChangedEvent();
        EditorPerfDiagnostics.RecordPhase(perf, notifyStart, "contentChanged.blocksChanged");

        if (perfStart != 0)
        {
            var ms = EditorPerfDiagnostics.ElapsedMs(perfStart);
            EditorPerfDiagnostics.ReportContentChange(ms);
            EditorPerfDiagnostics.RecordIfSlow(perf, "contentChanged", ms, $"top={_host.Blocks.Count} realized={_host.Collection.RealizedRowCount}");
        }
    }

    internal void OnBlockDeleteRequested(BlockViewModel block)
    {
        DeleteBlock(block);
    }

    internal void OnDeleteAndFocusAboveRequested(BlockViewModel block)
    {
        DeleteBlock(block);
    }

    internal void OnMergeWithPreviousRequested(BlockViewModel block)
    {
        var previousBlock = BlockHierarchy.FindPreviousInDocumentOrder(_host.Blocks, block);
        if (previousBlock == null) return;

        if (_host._pendingSnapshot == null)
            _host.BeginStructuralChange();
        var insertionPoint = previousBlock.Content?.Length ?? 0;
        var suffix = BlockEditorContentPolicy.MergeSuffixFromFollowingBlock(block.Content);
        previousBlock.Content = (previousBlock.Content ?? string.Empty) + suffix;

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            col.Remove(block);
            BlockHierarchy.ClearChildOwnership(block);
            _host.Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            if (col.Count == 0)
            {
                var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
                BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
                col.Add(ph);
                _host.Collection.SubscribeToBlock(ph);
            }
        }
        else
        {
            _host.Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            _host.Blocks.Remove(block);
        }

        _host.ReorderBlocks();
        _host.CommitStructuralChange("Merge blocks");
        _host.RaiseBlocksChangedEvent();

        var caretTarget = insertionPoint;
        Dispatcher.UIThread.Post(
            () =>
            {
                previousBlock.PendingCaretIndex = caretTarget;
                previousBlock.IsFocused = true;
            },
            DispatcherPriority.Input);
    }

    internal void OnExitSplitBelowRequested(BlockViewModel block, string? followingText)
    {
        if (block.OwnerTwoColumn is not TwoColumnBlockViewModel tc) return;
        var topIdx = _host.Blocks.IndexOf(tc);
        if (topIdx < 0) return;

        if (_host._pendingSnapshot == null)
            _host.BeginStructuralChange();

        var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var bodyEmpty = BlockEditorContentPolicy.IsVisuallyEmpty(block.Content ?? "");
        if (bodyEmpty && col.Contains(block))
        {
            col.Remove(block);
            BlockHierarchy.ClearChildOwnership(block);
            _host.Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            if (col.Count == 0)
            {
                var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
                BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
                col.Add(ph);
                _host.Collection.SubscribeToBlock(ph);
            }
        }

        var nb = BlockFactory.CreateBlock(BlockType.Text, 0);
        if (!string.IsNullOrEmpty(followingText))
            nb.Content = followingText;
        _host.Collection.SubscribeToBlock(nb);
        _host.Blocks.Insert(topIdx + 1, nb);
        _host.ReorderBlocks();
        _host.CommitStructuralChange("Exit split");
        _host.RaiseBlocksChangedEvent();
        Dispatcher.UIThread.Post(() => nb.IsFocused = true, DispatcherPriority.Input);
    }

    internal void DeleteBlock(BlockViewModel block)
    {
        if (_host._pendingSnapshot == null)
            _host.BeginStructuralChange();

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var ci = col.IndexOf(block);
            if (ci < 0) return;

            if (ShouldUnwrapTwoColumnBecauseAllCellsEmpty(tc, block))
            {
                var topIdx = _host.Blocks.IndexOf(tc);
                if (topIdx < 0) return;
                _host.Collection.UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
                _host.Blocks.RemoveAt(topIdx);
                var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
                _host.Collection.SubscribeToBlock(placeholder);
                _host.Blocks.Insert(topIdx, placeholder);
                _host.ReorderBlocks();
                _host.CommitStructuralChange("Unwrap split");
                _host.RaiseBlocksChangedEvent();
                Dispatcher.UIThread.Post(() => placeholder.IsFocused = true, DispatcherPriority.Input);
                return;
            }

            if (ShouldUnwrapTwoColumnBecauseOneColumnEmptyOtherHasContent(tc, block))
            {
                var filledColumnIsLeft = !ColumnIsEntirelyVisuallyEmpty(tc, true) &&
                    ColumnIsEntirelyVisuallyEmpty(tc, false);
                UnwrapTwoColumnPromotingFilledColumn(tc, filledColumnIsLeft);
                _host.CommitStructuralChange("Unwrap split");
                _host.RaiseBlocksChangedEvent();
                return;
            }

            if (col.Count == 1 && tc.LeftColumnBlocks.Count + tc.RightColumnBlocks.Count == 1)
            {
                var topIdx = _host.Blocks.IndexOf(tc);
                if (topIdx < 0) return;
                _host.Collection.UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
                _host.Blocks.RemoveAt(topIdx);
                var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
                _host.Collection.SubscribeToBlock(placeholder);
                _host.Blocks.Insert(topIdx, placeholder);
                _host.ReorderBlocks();
                _host.CommitStructuralChange("Delete block");
                _host.RaiseBlocksChangedEvent();
                Dispatcher.UIThread.Post(() => placeholder.IsFocused = true, DispatcherPriority.Input);
                return;
            }

            if (RemoveCellFromTwoColumnOrUnwrap(tc, block))
            {
                _host.CommitStructuralChange("Unwrap split");
                _host.RaiseBlocksChangedEvent();
                return;
            }

            _host.CommitStructuralChange("Delete block");
            _host.RaiseBlocksChangedEvent();
            var focusIdx = System.Math.Max(0, System.Math.Min(ci, col.Count - 1));
            Dispatcher.UIThread.Post(() => col[focusIdx].IsFocused = true, DispatcherPriority.Input);
            return;
        }

        var index = _host.Blocks.IndexOf(block);
        if (index == -1) return;

        if (_host.Blocks.Count == 1)
        {
            if (block.Type == BlockType.Image)
                _host.RegisterReleasedStoredImagePathCore(block.ImagePath);
            block.Content = string.Empty;
            block.Type = BlockType.Text;
            block.IsFocused = true;
            _host.UpdateListNumbers();
            _host.CommitStructuralChange("Clear block");
            return;
        }

        _host.Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        _host.Blocks.Remove(block);

        var targetIndex = index > 0 ? index - 1 : 0;
        if (_host.Blocks.Count > targetIndex)
        {
            Dispatcher.UIThread.Post(
                () => _host.Blocks[targetIndex].IsFocused = true,
                DispatcherPriority.Input);
        }

        _host.ReorderBlocks();
        _host.CommitStructuralChange("Delete block");
        _host.RaiseBlocksChangedEvent();
    }

    private static bool IsEffectivelyEmptyForSplitCollapse(BlockViewModel block)
    {
        if (block.Type == BlockType.Image)
        {
            if (!string.IsNullOrWhiteSpace(block.ImagePath)) return false;
            if (!string.IsNullOrWhiteSpace(block.Content)) return false;
            return true;
        }

        return BlockEditorContentPolicy.IsVisuallyEmpty(block.Content);
    }

    private static bool ShouldUnwrapTwoColumnBecauseAllCellsEmpty(TwoColumnBlockViewModel tc, BlockViewModel block)
    {
        if (!IsEffectivelyEmptyForSplitCollapse(block)) return false;
        foreach (var b in tc.LeftColumnBlocks)
        {
            if (ReferenceEquals(b, block)) continue;
            if (!IsEffectivelyEmptyForSplitCollapse(b)) return false;
        }
        foreach (var b in tc.RightColumnBlocks)
        {
            if (ReferenceEquals(b, block)) continue;
            if (!IsEffectivelyEmptyForSplitCollapse(b)) return false;
        }
        return true;
    }

    private static bool ColumnIsEntirelyVisuallyEmpty(TwoColumnBlockViewModel tc, bool leftColumn)
    {
        var col = leftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        foreach (var b in col)
        {
            if (!IsEffectivelyEmptyForSplitCollapse(b))
                return false;
        }
        return true;
    }

    private static bool ShouldUnwrapTwoColumnBecauseOneColumnEmptyOtherHasContent(TwoColumnBlockViewModel tc,
        BlockViewModel block)
    {
        if (!IsEffectivelyEmptyForSplitCollapse(block)) return false;
        if (ColumnIsEntirelyVisuallyEmpty(tc, true) && !ColumnIsEntirelyVisuallyEmpty(tc, false) && block.IsLeftColumn)
            return true;
        if (ColumnIsEntirelyVisuallyEmpty(tc, false) && !ColumnIsEntirelyVisuallyEmpty(tc, true) && !block.IsLeftColumn)
            return true;
        return false;
    }

    internal void UnwrapTwoColumnPromotingFilledColumn(TwoColumnBlockViewModel tc, bool filledColumnIsLeft)
    {
        var topIdx = _host.Blocks.IndexOf(tc);
        if (topIdx < 0) return;

        var filledCol = filledColumnIsLeft ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var emptyCol = filledColumnIsLeft ? tc.RightColumnBlocks : tc.LeftColumnBlocks;

        var promoted = filledCol.ToList();
        foreach (var b in emptyCol.ToList())
        {
            _host.Collection.UnsubscribeFromBlock(b, registerReleasedStoredImagePath: true);
            emptyCol.Remove(b);
        }
        foreach (var b in promoted)
        {
            _host.Collection.UnsubscribeFromBlock(b, registerReleasedStoredImagePath: false);
            filledCol.Remove(b);
            BlockHierarchy.ClearChildOwnership(b);
        }

        _host.Collection.UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: false);
        _host.Blocks.RemoveAt(topIdx);

        int insertAt = topIdx;
        foreach (var b in promoted)
        {
            _host.Collection.SubscribeToBlock(b);
            _host.Blocks.Insert(insertAt++, b);
        }

        _host.ReorderBlocks();
        var focus = promoted.Count > 0 ? promoted[0] : null;
        if (focus != null)
            Dispatcher.UIThread.Post(() => focus.IsFocused = true, DispatcherPriority.Input);
    }

    internal bool RemoveCellFromTwoColumnOrUnwrap(TwoColumnBlockViewModel tc, BlockViewModel block)
    {
        var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var ci = col.IndexOf(block);
        if (ci < 0) return false;

        _host.Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        col.RemoveAt(ci);
        BlockHierarchy.ClearChildOwnership(block);

        if (col.Count == 0)
        {
            var other = block.IsLeftColumn ? tc.RightColumnBlocks : tc.LeftColumnBlocks;
            if (other.Count > 0)
            {
                UnwrapTwoColumnPromotingFilledColumn(tc, !block.IsLeftColumn);
                return true;
            }

            var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
            BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
            col.Add(ph);
            _host.Collection.SubscribeToBlock(ph);
        }

        _host.ReorderBlocks();
        return false;
    }

    internal void TryCollapseSplitToSingleEmptyIfAllLeavesEmpty(TwoColumnBlockViewModel tc)
    {
        if (_host.Blocks.IndexOf(tc) < 0) return;
        foreach (var b in tc.LeftColumnBlocks)
        {
            if (!IsEffectivelyEmptyForSplitCollapse(b))
                return;
        }
        foreach (var b in tc.RightColumnBlocks)
        {
            if (!IsEffectivelyEmptyForSplitCollapse(b))
                return;
        }
        var topIdx = _host.Blocks.IndexOf(tc);
        if (topIdx < 0) return;
        _host.Collection.UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
        _host.Blocks.RemoveAt(topIdx);
        var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
        _host.Collection.SubscribeToBlock(placeholder);
        _host.Blocks.Insert(topIdx, placeholder);
        _host.ReorderBlocks();
    }

    internal void TryCollapseSplitAfterDragOut(TwoColumnBlockViewModel? tc)
    {
        if (tc == null || _host.Blocks.IndexOf(tc) < 0) return;
        TryCollapseSplitToSingleEmptyIfAllLeavesEmpty(tc);
        if (_host.Blocks.IndexOf(tc) < 0) return;
        if (IsNativeTwoColumn(tc)) return;
        if (ColumnIsEntirelyVisuallyEmpty(tc, true) && !ColumnIsEntirelyVisuallyEmpty(tc, false))
            UnwrapTwoColumnPromotingFilledColumn(tc, false);
        else if (ColumnIsEntirelyVisuallyEmpty(tc, false) && !ColumnIsEntirelyVisuallyEmpty(tc, true))
            UnwrapTwoColumnPromotingFilledColumn(tc, true);
    }

    private static bool IsNativeTwoColumn(TwoColumnBlockViewModel tc)
    {
        if (!tc.Meta.TryGetValue(BlockEditor.NativeTwoColumnMetaKey, out var raw) || raw == null)
            return false;
        if (raw is bool b)
            return b;
        if (raw is string s && bool.TryParse(s, out var parsed))
            return parsed;
        if (raw is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.True) return true;
            if (je.ValueKind == JsonValueKind.False) return false;
            if (je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var jsonParsed))
                return jsonParsed;
        }
        return false;
    }

}
