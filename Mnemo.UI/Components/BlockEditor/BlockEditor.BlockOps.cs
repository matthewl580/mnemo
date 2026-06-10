using Avalonia.Threading;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    public void AddBlock(BlockType type, int? position = null, string? initialContent = null)
    {
        var order = position ?? Blocks.Count;
        var block = BlockFactory.CreateBlock(type, order);
        if (initialContent != null)
            block.Content = initialContent;
        SubscribeToBlock(block);

        if (position.HasValue && position.Value < Blocks.Count)
            Blocks.Insert(position.Value, block);
        else
            Blocks.Add(block);

        ReorderBlocks();
    }

    /// <summary>Replaces a top-level block with a two-column row (empty text in each column). No-op if the block is not in <see cref="Blocks"/> or is inside a column.</summary>
    public void ReplaceBlockWithTwoColumn(BlockViewModel block)
    {
        if (block.OwnerTwoColumn != null) return;

        var index = Blocks.IndexOf(block);
        if (index < 0) return;

        BeginStructuralChange();

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        Blocks.RemoveAt(index);

        var tc = (TwoColumnBlockViewModel)BlockFactory.CreateBlock(BlockType.TwoColumn, block.Order);
        tc.Meta[NativeTwoColumnMetaKey] = true;
        SubscribeToBlock(tc);
        Blocks.Insert(index, tc);
        ReorderBlocks();
        CommitStructuralChange("Two columns");
        NotifyBlocksChanged();

        var left = tc.LeftColumnBlocks[0];
        left.PendingCaretIndex = 0;
        Dispatcher.UIThread.Post(() => left.IsFocused = true, DispatcherPriority.Render);
    }

    /// <summary>Appends an empty text block to a split column (tap below the column stack). Focuses the last cell if it is already empty.</summary>
    internal void TryAddEmptyBlockInSplitColumn(TwoColumnBlockViewModel tc, bool leftColumn)
    {
        if (Blocks.IndexOf(tc) < 0) return;

        var col = leftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        if (col.Count > 0 && BlockEditorContentPolicy.IsVisuallyEmpty(col[^1].Content))
        {
            var last = col[^1];
            last.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(() => last.IsFocused = true, DispatcherPriority.Input);
            return;
        }

        BeginStructuralChange();
        var newBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
        col.Add(newBlock);
        ReorderBlocks();
        CommitStructuralChange("New block in column");
        newBlock.PendingCaretIndex = 0;
        Dispatcher.UIThread.Post(() => newBlock.IsFocused = true, DispatcherPriority.Render);
    }

    /// <summary>
    /// Splits a text block at [start, end) into [before][equation][after] blocks.
    /// The selected text becomes the LaTeX source for the new equation block.
    /// </summary>
    public void SplitBlockIntoEquation(BlockViewModel block, int selStart, int selEnd)
    {
        var ownerSplit = block.OwnerTwoColumn;
        System.Collections.ObjectModel.ObservableCollection<BlockViewModel>? column = null;
        int index;
        if (ownerSplit != null)
        {
            column = block.IsLeftColumn ? ownerSplit.LeftColumnBlocks : ownerSplit.RightColumnBlocks;
            index = column.IndexOf(block);
        }
        else
            index = Blocks.IndexOf(block);
        if (index < 0) return;

        var runs = block.Spans;
        var selectedText = InlineSpanFormatApplier.Flatten(
            InlineSpanFormatApplier.SliceRuns(runs, selStart, selEnd));
        var latex = Core.Formatting.EquationLatexNormalizer.Normalize(selectedText);

        var beforeRuns = InlineSpanFormatApplier.SliceRuns(runs, 0, selStart);
        var afterRuns = InlineSpanFormatApplier.SliceRuns(runs, selEnd,
            InlineSpanFormatApplier.Flatten(runs).Length);

        BeginStructuralChange();

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        if (column != null)
        {
            column.RemoveAt(index);
            BlockHierarchy.ClearChildOwnership(block);
        }
        else
            Blocks.RemoveAt(index);

        int insertAt = index;

        void InsertSplitPart(BlockViewModel vm)
        {
            if (column != null)
            {
                BlockHierarchy.WireChildOwnership(ownerSplit!, vm, block.IsLeftColumn);
                column.Insert(insertAt++, vm);
            }
            else
            {
                SubscribeToBlock(vm);
                Blocks.Insert(insertAt++, vm);
            }
        }

        if (beforeRuns.Count > 0 && !string.IsNullOrEmpty(InlineSpanFormatApplier.Flatten(beforeRuns)))
        {
            var beforeVm = BlockFactory.CreateBlock(block.Type, 0);
            beforeVm.CommitSpansFromEditor(beforeRuns);
            InsertSplitPart(beforeVm);
        }

        var eqVm = BlockFactory.CreateBlock(BlockType.Equation, 0);
        eqVm.EquationLatex = latex;
        InsertSplitPart(eqVm);

        if (afterRuns.Count > 0 && !string.IsNullOrEmpty(InlineSpanFormatApplier.Flatten(afterRuns)))
        {
            var afterVm = BlockFactory.CreateBlock(block.Type, 0);
            afterVm.CommitSpansFromEditor(afterRuns);
            InsertSplitPart(afterVm);
        }

        ReorderBlocks();
        CommitStructuralChange("Split to equation");
        NotifyBlocksChanged();

        Dispatcher.UIThread.Post(() => eqVm.IsFocused = true, DispatcherPriority.Render);
    }

    internal void OnNewBlockRequested(BlockViewModel block, string? initialContent)
    {
        if (_pendingSnapshot == null)
            BeginStructuralChange();

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var i = col.IndexOf(block);
            if (i < 0) return;
            var newBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
            if (initialContent != null)
                newBlock.Content = initialContent;
            BlockHierarchy.WireChildOwnership(tc, newBlock, block.IsLeftColumn);
            col.Insert(i + 1, newBlock);
            ReorderBlocks();
            CommitStructuralChange("Split block");
            BlocksChanged?.Invoke();
            newBlock.PendingCaretIndex = 0;
            // Loaded (not Render): nested column repeaters realize the new cell a frame later;
            // focusing earlier finds no editor and the caret is lost.
            Dispatcher.UIThread.Post(() =>
            {
                newBlock.IsFocused = false;
                newBlock.IsFocused = true;
            }, DispatcherPriority.Loaded);
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index < 0) return;
        AddBlock(BlockType.Text, index + 1, initialContent);

        var newBlockIndex = index + 1;
        if (newBlockIndex < Blocks.Count)
        {
            var newBlock = Blocks[newBlockIndex];
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(
                () => newBlock.IsFocused = true,
                DispatcherPriority.Render);
        }

        CommitStructuralChange("Split block");
        BlocksChanged?.Invoke();
    }

    internal void OnNewBlockAboveRequested(BlockViewModel block, string? initialContent)
    {
        if (_pendingSnapshot == null)
            BeginStructuralChange();

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var i = col.IndexOf(block);
            if (i < 0) return;
            var newBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
            if (initialContent != null)
                newBlock.Content = initialContent;
            BlockHierarchy.WireChildOwnership(tc, newBlock, block.IsLeftColumn);
            col.Insert(i, newBlock);
            ReorderBlocks();
            CommitStructuralChange("Insert block above");
            BlocksChanged?.Invoke();
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(() =>
            {
                newBlock.IsFocused = false;
                newBlock.IsFocused = true;
            }, DispatcherPriority.Loaded);
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index < 0) return;
        AddBlock(BlockType.Text, index, initialContent);

        if (Blocks.IndexOf(block) < 0) return;

        if (!block.PendingCaretIndex.HasValue)
            block.PendingCaretIndex = 0;

        CommitStructuralChange("Insert block above");
        BlocksChanged?.Invoke();

        Dispatcher.UIThread.Post(() =>
        {
            if (!block.PendingCaretIndex.HasValue)
                block.PendingCaretIndex = 0;
            block.IsFocused = false;
            block.IsFocused = true;
        }, DispatcherPriority.Loaded);
    }

    internal void OnNewBlockOfTypeRequested(BlockViewModel block, BlockType type, string? initialContent)
    {
        BeginStructuralChange();

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var i = col.IndexOf(block);
            if (i < 0) return;
            var newBlock = BlockFactory.CreateBlock(type, 0);
            if (initialContent != null)
                newBlock.Content = initialContent;
            BlockHierarchy.WireChildOwnership(tc, newBlock, block.IsLeftColumn);
            col.Insert(i + 1, newBlock);
            ReorderBlocks();
            CommitStructuralChange("New block");
            BlocksChanged?.Invoke();
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(() =>
            {
                newBlock.IsFocused = false;
                newBlock.IsFocused = true;
            }, DispatcherPriority.Loaded);
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index < 0) return;
        AddBlock(type, index + 1, initialContent);

        var newBlockIndex = index + 1;
        if (newBlockIndex < Blocks.Count)
        {
            var newBlock = Blocks[newBlockIndex];
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(
                () => newBlock.IsFocused = true,
                DispatcherPriority.Render);
        }

        CommitStructuralChange("New block");
        BlocksChanged?.Invoke();
    }

    internal void OnDuplicateBlockRequested(BlockViewModel block)
    {
        _ = DuplicateImageBlockAsync(block);
    }

    private async Task DuplicateImageBlockAsync(BlockViewModel block)
    {
        if (block.Type != BlockType.Image) return;
        var svc = ResolveImageAssetService();
        if (svc == null) return;

        var srcPath = block.ImagePath;
        if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;

        var index = Blocks.IndexOf(block);
        if (index < 0) return;

        var newVm = BlockFactory.CreateBlock(BlockType.Image, 0);
        var result = await svc.ImportAndCopyAsync(srcPath, newVm.Id).ConfigureAwait(true);
        if (!result.IsSuccess || string.IsNullOrEmpty(result.Value)) return;

        BeginStructuralChange();

        newVm.ImagePath = result.Value;
        newVm.ImageWidth = block.ImageWidth;
        newVm.ImageAlign = string.IsNullOrEmpty(block.ImageAlign) ? "left" : block.ImageAlign;
        newVm.SetSpans(new List<InlineSpan>(block.Spans));

        SubscribeToBlock(newVm);
        Blocks.Insert(index + 1, newVm);
        ReorderBlocks();
        ClearBlockSelection();
        CommitStructuralChange("Duplicate image block");
        BlocksChanged?.Invoke();

        Dispatcher.UIThread.Post(() => newVm.IsFocused = true, DispatcherPriority.Input);
    }

    internal void OnFocusPreviousRequested(BlockViewModel block, double? caretPixelX)
    {
        var prev = BlockHierarchy.FindPreviousInDocumentOrder(Blocks, block);
        if (prev == null) return;
        block.IsFocused = false;
        if (caretPixelX.HasValue)
        {
            prev.PendingCaretPixelX = caretPixelX.Value;
            prev.PendingCaretPlaceOnLastLine = true;
        }
        Dispatcher.UIThread.Post(() => prev.IsFocused = true, DispatcherPriority.Input);
    }

    internal void OnFocusNextRequested(BlockViewModel block, double? caretPixelX)
    {
        var next = BlockHierarchy.FindNextInDocumentOrder(Blocks, block);
        if (next == null) return;
        block.IsFocused = false;
        if (caretPixelX.HasValue)
        {
            next.PendingCaretPixelX = caretPixelX.Value;
            next.PendingCaretPlaceOnLastLine = false;
        }
        Dispatcher.UIThread.Post(() => next.IsFocused = true, DispatcherPriority.Input);
    }

    internal void ReorderBlocks()
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            Blocks[i].Order = i;
            if (Blocks[i] is TwoColumnBlockViewModel tc)
            {
                for (int j = 0; j < tc.LeftColumnBlocks.Count; j++)
                    tc.LeftColumnBlocks[j].Order = j;
                for (int j = 0; j < tc.RightColumnBlocks.Count; j++)
                    tc.RightColumnBlocks[j].Order = j;
            }
        }

        if (_numberedListBlocks.Count > 0)
            UpdateListNumbers();
    }

    internal void UpdateListNumbers()
    {
        if (_numberedListBlocks.Count == 0)
            return;

        int listNumber = 1;
        bool prevWasNumbered = false;
        foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (block.Type == BlockType.NumberedList)
            {
                if (!prevWasNumbered)
                    listNumber = System.Math.Max(1, block.ListNumberIndex);
                block.ListNumberIndex = listNumber++;
                prevWasNumbered = true;
            }
            else
            {
                listNumber = 1;
                prevWasNumbered = false;
            }
        }
    }
}
