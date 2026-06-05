using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Layout;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.History;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Common;
using Mnemo.UI.Components.BlockEditor.History;
using Mnemo.UI.Services;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    #region Clipboard and block-selection keyboard (copy as markdown, paste as blocks, backspace deletes selection)

    /// <summary>
    /// Block kinds that should replace the current block type when pasted at the start of a rich block
    /// (e.g. "# Title" must become a heading, not literal text in a Text block).
    /// </summary>
    private static bool IsStructuralBlockTypeForLineStartPaste(BlockType t) =>
        t is BlockType.Heading1 or BlockType.Heading2 or BlockType.Heading3 or BlockType.Heading4
        or BlockType.BulletList or BlockType.NumberedList or BlockType.Checklist
        or BlockType.Quote;

    /// <summary>Image/Divider/Equation blocks have no inline runs â€” merging them into a Text block drops the payload.</summary>
    private static bool PasteFirstBlockRequiresBlockInsert(BlockViewModel[] pasted) =>
        pasted.Length > 0 && pasted[0].Type is BlockType.Image or BlockType.Divider or BlockType.Equation or BlockType.Page;

    /// <summary>
    /// Applies pasted block type and body runs, then list/checklist metadata. Runs are committed first so
    /// <see cref="BlockViewModel.Type"/>'s heading path runs on the final run list.
    /// </summary>
    private static void ApplyPastedStructuralBlockToViewModel(BlockViewModel target, BlockViewModel pastedFirst)
    {
        target.CommitSpansFromEditor(InlineSpanFormatApplier.Normalize(pastedFirst.CloneSpans()));
        target.Type = pastedFirst.Type;
        if (pastedFirst.Type == BlockType.NumberedList)
            target.ListNumberIndex = pastedFirst.ListNumberIndex;
        if (pastedFirst.Type == BlockType.Checklist)
            target.IsChecked = pastedFirst.IsChecked;
        if (pastedFirst.Type == BlockType.Page)
            target.ReferenceNoteId = pastedFirst.ReferenceNoteId;
    }

    /// <summary>
    /// Nested <see cref="TextBox"/> (e.g. code) uses OS copy/cut/paste and undo; we must not steal those shortcuts.
    /// Image caption uses <see cref="RichTextEditor"/> â€” it has no built-in undo/clipboard, so those stay on the editor.
    /// </summary>
    private bool IsFocusInsideNestedTextBox()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.FocusManager?.GetFocusedElement() is not TextBox tb)
            return false;
        return this.IsVisualAncestorOf(tb);
    }

    private bool IsImageCaptionRichEditorFocused()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.FocusManager?.GetFocusedElement() is not Visual v)
            return false;
        if (!this.IsVisualAncestorOf(v)) return false;
        var rte = v as RichTextEditor ?? v.GetVisualAncestors().OfType<RichTextEditor>().FirstOrDefault();
        return rte != null && rte.Tag is string tag && tag == "BlockEditorImageCaption";
    }

    /// <summary>Ctrl+A should select all <em>blocks</em> unless focus is in a nested text field or image caption.</summary>
    private bool ShouldDeferSelectAllBlocksShortcut() =>
        IsFocusInsideNestedTextBox() || IsImageCaptionRichEditorFocused();

    /// <summary>Invoked from the workspace keybind router when <c>editor.clipboard.*</c> matches (tunnel, before <see cref="Editor_KeyDown"/>).</summary>
    public bool TryHandlePasteKeybind()
    {
        if (IsReadOnly)
            return false;
        if (IsFocusInsideNestedTextBox())
            return false;
        var hasBlockSelection = _selectedBlockCount > 0;
        _ = TryPasteFromClipboardAsync(hasBlockSelection);
        return true;
    }

    /// <summary>Invoked from the workspace keybind router for <c>editor.clipboard.copy</c>.</summary>
    public bool TryHandleCopyKeybind()
    {
        if (IsReadOnly)
            return false;
        if (IsFocusInsideNestedTextBox())
            return false;
        _ = TryCopySelectionToClipboardAsync();
        return true;
    }

    /// <summary>Invoked from the workspace keybind router for <c>editor.clipboard.cut</c>.</summary>
    public bool TryHandleCutKeybind()
    {
        if (IsReadOnly)
            return false;
        if (IsFocusInsideNestedTextBox())
            return false;
        _ = TryCutSelectionAsync();
        return true;
    }

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;

        bool ctrlF = e.Key == Key.F && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlF)
        {
            OpenFindPanel();
            TryPopulateFindQueryFromFocusedSelection();
            e.Handled = true;
            return;
        }

        if (HandleFindPanelNavigationKey(e))
            return;

        if (IsReadOnly)
            return;

        var hasBlockSelection = _selectedBlockCount > 0;
        bool deferClipboardToNestedTextBox = IsFocusInsideNestedTextBox();
        bool deferUndoRedoToNestedTextBox = IsFocusInsideNestedTextBox();

        // 1. Backspace / Delete: delete all selected blocks, or delete text selection (including cross-block)
        if ((e.Key == Key.Back || e.Key == Key.Delete) && hasBlockSelection)
        {
            DeleteSelectedBlocks();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Back && !hasBlockSelection && HasCrossBlockTextSelection())
        {
            TryDeleteTextSelection();
            e.Handled = true;
            return;
        }

        // 2. Ctrl+C: copy selected blocks (or cross-block text selection) as markdown + Mnemo JSON.
        // Mark handled immediately: clipboard writes are async and may yield; if Handled stays false,
        // routing continues and the OS / other handlers can put plain text on the clipboard and wipe our payload.
        bool ctrlC = e.Key == Key.C && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlC && !deferClipboardToNestedTextBox)
        {
            e.Handled = true;
            _ = TryCopySelectionToClipboardAsync();
            return;
        }

        bool ctrlX = e.Key == Key.X && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlX && !deferClipboardToNestedTextBox)
        {
            e.Handled = true;
            _ = TryCutSelectionAsync();
            return;
        }

        // 3. Ctrl+V: paste markdown as blocks (replacing block selection when present)
        bool ctrlV = e.Key == Key.V && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlV && !deferClipboardToNestedTextBox)
        {
            e.Handled = true;
            _ = TryPasteFromClipboardAsync(hasBlockSelection);
            return;
        }

        // 4. Ctrl+Z: undo
        bool ctrlZ = e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control
                                     && (e.KeyModifiers & KeyModifiers.Shift) == 0;
        if (ctrlZ && !deferUndoRedoToNestedTextBox)
        {
            _ = UndoAsync();
            e.Handled = true;
            return;
        }

        // 5. Ctrl+Y / Ctrl+Shift+Z: redo
        bool ctrlY = e.Key == Key.Y && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        bool ctrlShiftZ = e.Key == Key.Z && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control
                                          && (e.KeyModifiers & KeyModifiers.Shift) != 0;
        if ((ctrlY || ctrlShiftZ) && !deferUndoRedoToNestedTextBox)
        {
            _ = RedoAsync();
            e.Handled = true;
            return;
        }
    }

    private void Editor_KeyDown_Bubble(object? sender, KeyEventArgs e)
    {
        if (IsReadOnly)
            return;
        if (e.Handled) return;

        // 6. Ctrl+A: select all blocks
        bool ctrlA = e.Key == Key.A && (e.KeyModifiers & KeyModifiers.Control) == KeyModifiers.Control;
        if (ctrlA && !ShouldDeferSelectAllBlocksShortcut())
        {
            ClearTextSelectionInAllBlocksExcept(null);
            foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
                block.IsSelected = true;
            e.Handled = true;
        }
    }


    /// <summary>
    /// Deletes all blocks that have IsSelected, then focuses the block at the first deleted index (or the one before).
    /// </summary>
    private void DeleteSelectedBlocks()
    {
        var doc = GetDocumentOrderBlocks();
        var toRemove = doc.Where(b => b.IsSelected).ToList();
        if (toRemove.Count == 0) return;

        BeginStructuralChange();

        var topLevel = toRemove.Where(b => b.OwnerTwoColumn == null).OrderByDescending(b => Blocks.IndexOf(b)).ToList();
        foreach (var block in topLevel)
        {
            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            Blocks.Remove(block);
        }

        var bySplit = new Dictionary<TwoColumnBlockViewModel, List<BlockViewModel>>();
        foreach (var block in toRemove.Where(b => b.OwnerTwoColumn is TwoColumnBlockViewModel))
        {
            var tc = (TwoColumnBlockViewModel)block.OwnerTwoColumn!;
            if (!bySplit.TryGetValue(tc, out var list))
            {
                list = new List<BlockViewModel>();
                bySplit[tc] = list;
            }
            list.Add(block);
        }

        foreach (var kv in bySplit)
        {
            var tc = kv.Key;
            var parts = kv.Value;
            if (Blocks.IndexOf(tc) < 0)
                continue;

            var hasLeft = parts.Any(b => b.IsLeftColumn);
            var hasRight = parts.Any(b => !b.IsLeftColumn);

            if (hasLeft && hasRight)
            {
                var topIdx = Blocks.IndexOf(tc);
                if (topIdx < 0) continue;
                UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
                Blocks.RemoveAt(topIdx);
                ReorderBlocks();
                continue;
            }

            if (hasLeft && !hasRight)
            {
                var allLeft = tc.LeftColumnBlocks.Count > 0 &&
                    tc.LeftColumnBlocks.All(b => parts.Contains(b));
                if (allLeft)
                {
                    UnwrapTwoColumnPromotingFilledColumn(tc, false);
                    continue;
                }

                foreach (var block in parts.OrderByDescending(b => tc.LeftColumnBlocks.IndexOf(b)))
                {
                    if (Blocks.IndexOf(tc) < 0) break;
                    if (!tc.LeftColumnBlocks.Contains(block)) continue;
                    RemoveCellFromTwoColumnOrUnwrap(tc, block);
                }
                continue;
            }

            if (hasRight && !hasLeft)
            {
                var allRight = tc.RightColumnBlocks.Count > 0 &&
                    tc.RightColumnBlocks.All(b => parts.Contains(b));
                if (allRight)
                {
                    UnwrapTwoColumnPromotingFilledColumn(tc, true);
                    continue;
                }

                foreach (var block in parts.OrderByDescending(b => tc.RightColumnBlocks.IndexOf(b)))
                {
                    if (Blocks.IndexOf(tc) < 0) break;
                    if (!tc.RightColumnBlocks.Contains(block)) continue;
                    RemoveCellFromTwoColumnOrUnwrap(tc, block);
                }
            }
        }

        if (Blocks.Count == 0)
        {
            var defaultBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
            SubscribeToBlock(defaultBlock);
            Blocks.Add(defaultBlock);
            Dispatcher.UIThread.Post(() => defaultBlock.IsFocused = true, DispatcherPriority.Input);
        }
        else
        {
            var focusTarget = BlockHierarchy.EnumerateInDocumentOrder(Blocks).FirstOrDefault();
            if (focusTarget != null)
                Dispatcher.UIThread.Post(() => focusTarget.IsFocused = true, DispatcherPriority.Input);
        }

        ReorderBlocks();
        ClearBlockSelection();
        CommitStructuralChange("Delete selected blocks");
        BlocksChanged?.Invoke();
    }

    public bool HasCrossBlockTextSelection()
    {
        foreach (var vm in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (GetEditableBlockForViewModel(vm)?.GetSelectionRange() != null) return true;
        }
        return false;
    }

    /// <summary>True if any block in the cross-block text selection overlaps linked text.</summary>
    public bool CrossBlockTextSelectionHasLink()
    {
        foreach (var vm in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            var eb = GetEditableBlockForViewModel(vm);
            if (eb?.GetSelectionRange() == null) continue;
            if (eb.DoesSelectionHaveLink()) return true;
        }
        return false;
    }

    /// <summary>First <c>LinkUrl</c> found in any block that has a text selection (cross-block link edit).</summary>
    public string? TryGetFirstLinkUrlInCrossBlockSelection()
    {
        foreach (var vm in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            var eb = GetEditableBlockForViewModel(vm);
            if (eb?.GetSelectionRange() is not { } sel) continue;
            var url = GetLinkUrlInRuns(vm.Spans, sel.start, sel.end);
            if (!string.IsNullOrEmpty(url)) return url;
        }
        return null;
    }

    private static string? GetLinkUrlInRuns(IReadOnlyList<InlineSpan> runs, int start, int end)
    {
        int pos = 0;
        foreach (var seg in runs)
        {
            if (seg is not TextSpan run)
            {
                pos += 1;
                continue;
            }

            int re = pos + run.Text.Length;
            if (re > start && pos < end && run.Style.LinkUrl != null)
                return run.Style.LinkUrl;
            pos = re;
        }
        return null;
    }

    public void ApplyInlineFormatToCrossBlockSelection(Mnemo.Core.Formatting.InlineFormatKind kind, string? color = null)
    {
        BeginStructuralChange();
        _isApplyingCrossBlockFormat = true;
        try
        {
            var doc = GetDocumentOrderBlocks();
            for (int i = 0; i < doc.Count; i++)
            {
                var editableBlock = GetEditableBlockForViewModel(doc[i]);
                if (editableBlock?.GetSelectionRange() != null)
                    editableBlock.ApplyInlineFormatInternal(kind, color);
            }
        }
        finally
        {
            _isApplyingCrossBlockFormat = false;
        }

        CommitStructuralChange("Format Selection");
    }

    private void TryDeleteTextSelection()
    {
        BlockViewModel? firstAffected = null;
        foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            var editableBlock = GetEditableBlockForViewModel(block);
            if (editableBlock?.GetSelectionRange() != null)
            {
                firstAffected ??= block;
                editableBlock.DeleteSelection();
            }
        }

        RemoveEmptyBlocksAfterTextDelete(firstAffected);
    }

    /// <summary>
    /// Removes empty blocks after a text-delete operation, but keeps the first block that had
    /// selection (even if empty) and focuses it. Other empty blocks are removed.
    /// </summary>
    private void RemoveEmptyBlocksAfterTextDelete(BlockViewModel? firstBlockInDeletion)
    {
        BeginStructuralChange();

        var docOrder = BlockHierarchy.EnumerateInDocumentOrder(Blocks).ToList();
        var toRemove = new List<BlockViewModel>();
        foreach (var block in docOrder)
        {
            if (block.Type is BlockType.TwoColumn or BlockType.Divider)
                continue;
            if (!BlockEditorContentPolicy.IsVisuallyEmpty(block.Content))
                continue;
            if (firstBlockInDeletion != null && ReferenceEquals(block, firstBlockInDeletion))
                continue;
            toRemove.Add(block);
        }

        var toRemoveSet = new HashSet<BlockViewModel>(toRemove);
        for (int i = docOrder.Count - 1; i >= 0; i--)
        {
            var block = docOrder[i];
            if (!toRemoveSet.Contains(block)) continue;
            RemoveEmptyBlockCellForTextDelete(block);
        }

        if (Blocks.Count == 0)
        {
            var defaultBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
            SubscribeToBlock(defaultBlock);
            Blocks.Add(defaultBlock);
            Dispatcher.UIThread.Post(
                () => defaultBlock.IsFocused = true,
                DispatcherPriority.Input);
        }
        else
        {
            var focusTarget = firstBlockInDeletion != null && BlockHierarchy.FindById(Blocks, firstBlockInDeletion.Id) != null
                ? firstBlockInDeletion
                : BlockHierarchy.EnumerateInDocumentOrder(Blocks).FirstOrDefault();
            if (focusTarget != null)
            {
                Dispatcher.UIThread.Post(
                    () => focusTarget.IsFocused = true,
                    DispatcherPriority.Input);
            }
        }

        ReorderBlocks();
        ClearBlockSelection();
        CommitStructuralChange("Delete text selection");
        BlocksChanged?.Invoke();
    }

    private void RemoveEmptyBlockCellForTextDelete(BlockViewModel block)
    {
        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var ci = col.IndexOf(block);
            if (ci < 0) return;

            if (col.Count == 1 && tc.LeftColumnBlocks.Count + tc.RightColumnBlocks.Count == 1)
            {
                var topIdx = Blocks.IndexOf(tc);
                if (topIdx < 0) return;
                UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
                Blocks.RemoveAt(topIdx);
                var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
                SubscribeToBlock(placeholder);
                Blocks.Insert(topIdx, placeholder);
                ReorderBlocks();
                return;
            }

            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            col.RemoveAt(ci);
            BlockHierarchy.ClearChildOwnership(block);
            if (col.Count == 0)
            {
                var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
                BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
                col.Add(ph);
                SubscribeToBlock(ph);
            }
            ReorderBlocks();
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index == -1) return;

        if (Blocks.Count == 1)
        {
            block.Content = string.Empty;
            block.Type = BlockType.Text;
            return;
        }

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        Blocks.Remove(block);
        ReorderBlocks();
    }

    private async Task TryCopySelectionToClipboardAsync()
    {
        await TryCopySelectionToClipboardCoreAsync();
    }

    private async Task TryCutSelectionAsync()
    {
        var copied = await TryCopySelectionToClipboardCoreAsync();
        if (!copied) return;

        var hasBlockSelection = _selectedBlockCount > 0;
        if (hasBlockSelection)
        {
            DeleteSelectedBlocks();
            return;
        }

        if (HasCrossBlockTextSelection())
        {
            TryDeleteTextSelection();
            return;
        }

        var focusVm = BlockHierarchy.FindFocused(Blocks);
        if (focusVm == null) return;
        var ed = GetEditableBlockForViewModel(focusVm);
        if (ed?.GetSelectionRange() is { } range && range.start < range.end)
            ed.DeleteSelection();
    }

    /// <returns>True if clipboard was written.</returns>
    private async Task<bool> TryCopySelectionToClipboardCoreAsync()
    {
        FlushTypingBatch();

        // Mode 2: block selection (drag-box)
        var selectedBlocks = BlockHierarchy.EnumerateInDocumentOrder(Blocks).Where(b => b.IsSelected).ToList();
        if (selectedBlocks.Count > 0)
        {
            await WriteBlocksToClipboardAsync(selectedBlocks);
            return true;
        }

        var toCopy = new List<BlockViewModel>();
        var docList = GetDocumentOrderBlocks();
        for (int i = 0; i < docList.Count; i++)
        {
            var editableBlock = GetEditableBlockForViewModel(docList[i]);
            if (editableBlock == null) continue;
            var range = editableBlock.GetSelectionRange();
            if (range == null || range.Value.start >= range.Value.end) continue;
            var block = docList[i];
            int start = range.Value.start, end = range.Value.end;
            var liveRuns = GetLiveRunsForBlock(block);
            var sliceRuns = InlineSpanFormatApplier.SliceRuns(liveRuns, start, end);
            // Slices from image captions are plain text; never emit a pseudo-Image block (would serialize as full image).
            var exportType = block.Type == BlockType.Image ? BlockType.Text : block.Type;
            var vm = BlockFactory.CreateBlock(exportType, toCopy.Count);
            vm.SetSpans(sliceRuns);
            if (block.Type == BlockType.Checklist)
                vm.IsChecked = block.IsChecked;
            if (block.Type == BlockType.NumberedList)
                vm.ListNumberIndex = block.ListNumberIndex;
            toCopy.Add(vm);
        }

        if (toCopy.Count > 0)
        {
            await WriteBlocksToClipboardAsync(toCopy);
            return true;
        }

        // Mode 3: focused block (caret-only or no cross-block selection)
        var fb = BlockHierarchy.FindFocused(Blocks);
        if (fb != null)
        {
            var b = fb;
            if (b.Type == BlockType.Image)
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard == null) return false;
                SyncViewModelsFromRichEditors(new[] { b });
                await topLevel.Clipboard.SetTextAsync(b.Content ?? string.Empty).ConfigureAwait(true);
                return true;
            }
            if (b.Type != BlockType.Divider)
            {
                await WriteBlocksToClipboardAsync(new List<BlockViewModel> { b });
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<InlineSpan> GetLiveRunsForBlock(BlockViewModel block)
    {
        var rte = GetEditableBlockForViewModel(block)?.TryGetRichTextEditor();
        if (rte?.Spans != null)
            return InlineSpanFormatApplier.Normalize(new List<InlineSpan>(rte.Spans));
        return block.Spans;
    }

    /// <summary>Push live <see cref="RichTextEditor.Spans"/> into the view model so clipboard serialization matches the editor.</summary>
    private void SyncViewModelsFromRichEditors(IEnumerable<BlockViewModel> blocks)
    {
        foreach (var b in blocks)
        {
            var rte = GetEditableBlockForViewModel(b)?.TryGetRichTextEditor();
            if (rte == null) continue;
            b.SetSpans(InlineSpanFormatApplier.Normalize(new List<InlineSpan>(rte.Spans)));
        }
    }

    private async Task WriteBlocksToClipboardAsync(IReadOnlyList<BlockViewModel> blocks)
    {
        if (blocks.Count == 0) return;
        SyncViewModelsFromRichEditors(blocks);
        var markdown = BlockMarkdownSerializer.Serialize(blocks);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard == null) return;

        Bitmap? singleImageBitmap = null;
        if (blocks.Count == 1 && blocks[0].Type == BlockType.Image)
        {
            var p = blocks[0].ImagePath;
            if (!string.IsNullOrEmpty(p) && File.Exists(p))
            {
                try
                {
                    singleImageBitmap = new Bitmap(p);
                }
                catch
                {
                    singleImageBitmap = null;
                }
            }
        }

        try
        {
            if (NoteClipboardService != null && NoteClipboardCodec != null)
            {
                var doc = EditorClipboardMapper.ToDocument(blocks);
                var json = NoteClipboardCodec.Serialize(doc);
                EditorClipboardDiagnostics.Log($"Copy {blocks.Count} block(s); md preview: {Truncate(markdown, 120)}");
                for (int bi = 0; bi < blocks.Count; bi++)
                    EditorClipboardDiagnostics.Log($"  block[{bi}] type={blocks[bi].Type} {EditorClipboardDiagnostics.SummarizeSpans(blocks[bi].Spans)}");
                await NoteClipboardService.WriteAsync(topLevel.Clipboard, markdown, json, singleImageBitmap).ConfigureAwait(true);
            }
            else if (singleImageBitmap != null)
                await topLevel.Clipboard.SetBitmapAsync(singleImageBitmap).ConfigureAwait(true);
            else
                await topLevel.Clipboard.SetTextAsync(markdown).ConfigureAwait(true);
        }
        catch (Exception)
        {
        }
        finally
        {
            singleImageBitmap?.Dispose();
        }
    }

    /// <summary>First index for inserting blocks after the focused cell: column stack after the cell, or top-level after the row.</summary>
    private static int GetPasteSiblingInsertStartIndex(BlockViewModel focusedVm, int topInsert) =>
        focusedVm.OwnerTwoColumn is TwoColumnBlockViewModel tc
        && (focusedVm.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks) is { } col
        && col.IndexOf(focusedVm) is >= 0 and var cellIdx
            ? cellIdx + 1
            : topInsert + 1;

    /// <summary>Inserts a block after the focused cell in-document: into the split column when focused is in a column, otherwise into <see cref="Blocks"/>.</summary>
    private void InsertPasteSiblingBlock(BlockViewModel focusedVm, ref int insertAt, BlockViewModel block)
    {
        if (focusedVm.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = focusedVm.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            int ci = col.IndexOf(focusedVm);
            if (ci >= 0)
            {
                col.Insert(insertAt, block);
                insertAt++;
                return;
            }
        }

        Blocks.Insert(insertAt, block);
        block.Order = insertAt;
        insertAt++;
    }

    private async Task TryPasteFromClipboardAsync(bool replaceBlockSelection)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null) return;

            string? text = null;
            string? mnemoJson = null;
            if (NoteClipboardService != null)
            {
                var read = await NoteClipboardService.ReadAsync(topLevel.Clipboard).ConfigureAwait(true);
                mnemoJson = read.MnemoJson;
                text = read.Text;
            }
            else
                text = await topLevel.Clipboard.TryGetTextAsync().ConfigureAwait(true);

            BlockViewModel[] pasted;
            if (NoteClipboardCodec != null &&
                !string.IsNullOrEmpty(mnemoJson) &&
                NoteClipboardCodec.TryDeserialize(mnemoJson, out var document) &&
                document != null)
            {
                EditorClipboardDiagnostics.Log($"Paste: Mnemo JSON path, blocks={document.Blocks?.Count ?? 0}");
                pasted = EditorClipboardMapper.ToViewModels(document, 0).ToArray();
            }
            else
            {
                var fromSystem = await TryPasteImageBlocksFromSystemClipboardAsync(topLevel.Clipboard, text).ConfigureAwait(true);
                if (fromSystem != null)
                {
                    EditorClipboardDiagnostics.Log($"Paste: system clipboard image / file path, blocks={fromSystem.Length}");
                    pasted = fromSystem;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(text)) return;
                    EditorClipboardDiagnostics.Log($"Paste: markdown fallback, textLen={text.Length} preview={Truncate(text, 120)}");
                    pasted = BlockMarkdownSerializer.Deserialize(text);
                }
            }

            if (pasted.Length == 0) return;

            await HydratePastedImageBlocksAsync(pasted).ConfigureAwait(true);
            if (pasted.Length > 0)
                EditorClipboardDiagnostics.Log($"Paste: first block type={pasted[0].Type} {EditorClipboardDiagnostics.SummarizeSpans(pasted[0].Spans)}");

            BeginStructuralChange();

            if (!replaceBlockSelection && pasted.Length >= 1)
            {
                var focusedVm = BlockHierarchy.FindFocused(Blocks);
                if (focusedVm != null)
                {
                    var topInsert = BlockHierarchy.GetTopLevelIndex(Blocks, focusedVm);
                    if (topInsert >= 0)
                    {
                    var canInlinePaste = focusedVm.Type != BlockType.Divider && !PasteFirstBlockRequiresBlockInsert(pasted);
                    // Image caption: only merge plain text; never turn the image into a heading/list via structural paste.
                    if (focusedVm.Type == BlockType.Image)
                        canInlinePaste = canInlinePaste && pasted.Length == 1 && pasted[0].Type == BlockType.Text;

                    if (canInlinePaste)
                    {
                        var editableBlock = GetEditableBlockForViewModel(focusedVm);
                        var range = editableBlock?.GetSelectionOrCaretRange();
                        if (range.HasValue)
                        {
                            var rtePaste = editableBlock?.TryGetRichTextEditor();
                            var content = rtePaste?.Text ?? focusedVm.Content ?? string.Empty;
                            int start = Math.Clamp(range.Value.start, 0, content.Length);
                            int end = Math.Clamp(range.Value.end, 0, content.Length);
                            string textBefore = content[0..start];
                            string textAfter = content[end..];
                            string firstContent = pasted[0].Content ?? string.Empty;

                            if (focusedVm.Type == BlockType.Code)
                            {
                                // Keep single inline paste in the same line/block.
                                if (pasted.Length == 1)
                                {
                                    focusedVm.Content = textBefore + firstContent + textAfter;
                                    editableBlock!.SetCaretIndex(textBefore.Length + firstContent.Length);
                                    ClearBlockSelection();
                                    CommitStructuralChange("Paste");
                                    BlocksChanged?.Invoke();
                                    return;
                                }

                                focusedVm.Content = textBefore + firstContent;
                                editableBlock!.SetCaretIndex(textBefore.Length + firstContent.Length);

                                int insertAtCode = GetPasteSiblingInsertStartIndex(focusedVm, topInsert);
                                for (int i = 1; i < pasted.Length; i++)
                                {
                                    var block = pasted[i];
                                    string blockContent = (block.Content ?? string.Empty) +
                                        (i == pasted.Length - 1 ? textAfter : string.Empty);
                                    block.Content = blockContent;
                                    SubscribeToBlock(block);
                                    InsertPasteSiblingBlock(focusedVm, ref insertAtCode, block);
                                }
                                ReorderBlocks();
                                ClearBlockSelection();
                                CommitStructuralChange("Paste");
                                BlocksChanged?.Invoke();
                                return;
                            }

                            var liveRunsForPaste = GetLiveRunsForBlock(focusedVm);
                            var tailRuns = InlineSpanFormatApplier.SliceRuns(liveRunsForPaste, end, content.Length);
                            var beforeRuns = InlineSpanFormatApplier.SliceRuns(liveRunsForPaste, 0, start);
                            bool promoteStructuralFirst =
                                InlineSpanFormatApplier.Flatten(beforeRuns).Length == 0
                                && IsStructuralBlockTypeForLineStartPaste(pasted[0].Type);

                            int caretAfterPaste;
                            if (promoteStructuralFirst)
                            {
                                ApplyPastedStructuralBlockToViewModel(focusedVm, pasted[0]);
                                caretAfterPaste = InlineSpanFormatApplier.Flatten(focusedVm.Spans).Length;
                            }
                            else
                            {
                                var pasteFirstRuns = pasted[0].CloneSpans();
                                var mergedFirst = (pasted.Length == 1 && pasted[0].Type == BlockType.Text)
                                    ? InlineSpanFormatApplier.Normalize(
                                        new List<InlineSpan>([..beforeRuns, ..pasteFirstRuns, ..tailRuns]))
                                    : InlineSpanFormatApplier.Normalize(
                                        new List<InlineSpan>([..beforeRuns, ..pasteFirstRuns]));
                                focusedVm.CommitSpansFromEditor(mergedFirst);
                                caretAfterPaste = start + InlineSpanFormatApplier.Flatten(pasteFirstRuns).Length;
                            }

                            editableBlock!.SetCaretIndex(caretAfterPaste);

                            if (pasted.Length == 1)
                            {
                                if (pasted[0].Type != BlockType.Text && InlineSpanFormatApplier.Flatten(tailRuns).Length > 0)
                                {
                                    var tailBlockRich = BlockFactory.CreateBlock(BlockType.Text, topInsert + 1);
                                    tailBlockRich.SetSpans(tailRuns);
                                    SubscribeToBlock(tailBlockRich);
                                    int insertAtTailRich = GetPasteSiblingInsertStartIndex(focusedVm, topInsert);
                                    InsertPasteSiblingBlock(focusedVm, ref insertAtTailRich, tailBlockRich);
                                    ReorderBlocks();
                                }
                                ClearBlockSelection();
                                CommitStructuralChange("Paste");
                                BlocksChanged?.Invoke();
                                return;
                            }

                            int insertAtRich = GetPasteSiblingInsertStartIndex(focusedVm, topInsert);
                            for (int i = 1; i < pasted.Length; i++)
                            {
                                var block = pasted[i];
                                if (i == pasted.Length - 1)
                                {
                                    var mergedLast = InlineSpanFormatApplier.Normalize(
                                        new List<InlineSpan>([..block.CloneSpans(), ..tailRuns]));
                                    block.SetSpans(mergedLast);
                                }
                                SubscribeToBlock(block);
                                InsertPasteSiblingBlock(focusedVm, ref insertAtRich, block);
                            }
                            ReorderBlocks();
                            ClearBlockSelection();
                            CommitStructuralChange("Paste");
                            BlocksChanged?.Invoke();
                            return;
                        }
                    }
                    }
                }
            }

            int insertIndex;
            if (replaceBlockSelection)
            {
                var selectedIndices = new List<int>();
                for (int i = 0; i < Blocks.Count; i++)
                    if (Blocks[i].IsSelected) selectedIndices.Add(i);
                if (selectedIndices.Count == 0)
                    insertIndex = GetFocusedBlockIndex() < 0 ? Blocks.Count : GetFocusedBlockIndex() + 1;
                else
                {
                    int firstIndex = selectedIndices.Min();
                    foreach (int i in selectedIndices.OrderByDescending(x => x))
                    {
                        var block = Blocks[i];
                        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
                        Blocks.RemoveAt(i);
                    }
                    insertIndex = firstIndex;
                }
            }
            else
            {
                var focusedVm = BlockHierarchy.FindFocused(Blocks);
                if (focusedVm?.OwnerTwoColumn is TwoColumnBlockViewModel)
                {
                    var insertAtInColumn = GetPasteSiblingInsertStartIndex(focusedVm, GetFocusedBlockIndex());
                    foreach (var block in pasted)
                    {
                        SubscribeToBlock(block);
                        InsertPasteSiblingBlock(focusedVm, ref insertAtInColumn, block);
                    }
                    ReorderBlocks();
                    ClearBlockSelection();
                    CommitStructuralChange("Paste");
                    BlocksChanged?.Invoke();

                    if (pasted.Length > 0)
                    {
                        var firstPasted = pasted[0];
                        Dispatcher.UIThread.Post(
                            () => firstPasted.IsFocused = true,
                            DispatcherPriority.Input);
                    }

                    return;
                }

                insertIndex = GetFocusedBlockIndex();
                if (insertIndex < 0) insertIndex = Blocks.Count;
                else insertIndex++;
            }

            foreach (var block in pasted)
            {
                SubscribeToBlock(block);
                Blocks.Insert(insertIndex, block);
                block.Order = insertIndex;
                insertIndex++;
            }
            ReorderBlocks();
            ClearBlockSelection();
            CommitStructuralChange("Paste");
            BlocksChanged?.Invoke();

            if (pasted.Length > 0)
            {
                var firstPasted = pasted[0];
                Dispatcher.UIThread.Post(
                    () => firstPasted.IsFocused = true,
                    DispatcherPriority.Input);
            }
        }
        catch (Exception)
        {
        }
    }

    private static readonly HashSet<string> ClipboardImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif"
    };

    private IImageAssetService? ResolveImageAssetService() =>
        ImageAssetService ?? (Application.Current as App)?.Services?.GetService(typeof(IImageAssetService)) as IImageAssetService;

    /// <summary>
    /// Last-block guard for clicking the area below all blocks: avoid stacking duplicate empty text blocks.
    /// Image blocks often have empty <see cref="BlockViewModel.Content"/> while the file path is on <see cref="BlockViewModel.ImagePath"/>.
    /// </summary>
    private static bool IsLastBlockEmptyForBelowBlocksAreaClick(IReadOnlyList<BlockViewModel> blocks)
    {
        if (blocks.Count == 0) return false;
        // Use the last leaf block in document order so a trailing two-column row
        // does not look "empty" just because the container itself has no Content.
        var last = BlockHierarchy.EnumerateInDocumentOrder(blocks).LastOrDefault() ?? blocks[blocks.Count - 1];
        if (last.Type == BlockType.Image)
        {
            if (!string.IsNullOrWhiteSpace(last.ImagePath)) return false;
            if (!string.IsNullOrWhiteSpace(last.Content)) return false;
        }

        return BlockEditorContentPolicy.IsVisuallyEmpty(last.Content);
    }

    private static string GetBlockMetaString(BlockViewModel vm, string key)
    {
        if (!vm.Meta.TryGetValue(key, out var val) || val == null) return string.Empty;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString() ?? string.Empty;
        return val.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Marks a path under the app images directory as no longer shown in the document (e.g. image replaced).
    /// The file is kept until <see cref="IHistoryManager.Cleared"/> and then removed if still unused.
    /// </summary>
    public void RegisterReleasedStoredImagePath(string? path)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RegisterReleasedStoredImagePath(path));
            return;
        }

        RegisterReleasedStoredImagePathCore(path);
    }

    private void RegisterReleasedStoredImagePathCore(string? path)
    {
        var n = NormalizePathForImageCompare(path);
        if (n == null || !MnemoAppPaths.IsPathUnderImagesDirectory(n)) return;
        _releasedStoredImagePaths.Add(n);
    }

    private void ReconcileReleasedStoredImagePathsWithDocument()
    {
        var referenced = CollectReferencedStoredImagePathsNormalized();
        _releasedStoredImagePaths.RemoveWhere(referenced.Contains);
    }

    private static string? NormalizePathForImageCompare(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// After <see cref="IHistoryManager.Clear"/>, undo cannot restore prior edits; delete stored image files
    /// that were explicitly released during this session and are still not referenced by the document.
    /// </summary>
    private void OnHistoryManagerCleared()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnHistoryManagerCleared, DispatcherPriority.Normal);
            return;
        }

        var referenced = CollectReferencedStoredImagePathsNormalized();
        _ = DeleteReleasedStoredImagesAfterHistoryClearedAsync(referenced);
    }

    private HashSet<string> CollectReferencedStoredImagePathsNormalized()
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (b.Type != BlockType.Image) continue;
            var n = NormalizePathForImageCompare(b.ImagePath);
            if (n != null)
                referenced.Add(n);
        }

        return referenced;
    }

    private async Task DeleteReleasedStoredImagesAfterHistoryClearedAsync(HashSet<string> referencedNormalizedPaths)
    {
        var svc = ResolveImageAssetService();
        if (svc == null) return;

        foreach (var path in _releasedStoredImagePaths.ToArray())
        {
            if (referencedNormalizedPaths.Contains(path))
                continue;
            try
            {
                await svc.DeleteStoredFileAsync(path, default).ConfigureAwait(false);
                _releasedStoredImagePaths.Remove(path);
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static double GetBlockMetaDouble(BlockViewModel vm, string key)
    {
        if (!vm.Meta.TryGetValue(key, out var val)) return 0;
        if (val is double d) return d;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number) return je.GetDouble();
        if (double.TryParse(val?.ToString(), out var p)) return p;
        return 0;
    }

    private async Task HydratePastedImageBlocksAsync(BlockViewModel[] pasted)
    {
        var svc = ResolveImageAssetService();
        if (svc == null) return;
        foreach (var vm in pasted)
        {
            if (vm.Type != BlockType.Image) continue;
            var path = vm.ImagePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(fileName, vm.Id, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var r = await svc.ImportAndCopyAsync(path, vm.Id).ConfigureAwait(true);
                if (r.IsSuccess && !string.IsNullOrEmpty(r.Value))
                    vm.ImagePath = r.Value!;
            }
            catch
            {
                // Keep original path if import fails (e.g. locked file).
            }
        }
    }

    private async Task<BlockViewModel[]?> TryPasteImageBlocksFromSystemClipboardAsync(IClipboard clipboard, string? textHint)
    {
        try
        {
            var files = await clipboard.TryGetFilesAsync().ConfigureAwait(true);
            if (files != null)
            {
                foreach (var f in files)
                {
                    var p = f.TryGetLocalPath();
                    if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                    if (!ClipboardImageExtensions.Contains(Path.GetExtension(p))) continue;
                    return new[] { CreateImageBlockStubForPaste(p) };
                }
            }
        }
        catch
        {
            // fall through
        }

        var bmp = await clipboard.TryGetBitmapAsync().ConfigureAwait(true);
        if (bmp != null)
        {
            try
            {
                return new[] { await SaveClipboardBitmapToNewImageBlockAsync(bmp).ConfigureAwait(true) };
            }
            finally
            {
                bmp.Dispose();
            }
        }

        var pathFromText = NormalizeSingleLineImagePathFromClipboard(textHint);
        if (pathFromText != null && File.Exists(pathFromText) &&
            ClipboardImageExtensions.Contains(Path.GetExtension(pathFromText)))
            return new[] { CreateImageBlockStubForPaste(pathFromText) };

        return null;
    }

    private static BlockViewModel CreateImageBlockStubForPaste(string pathOrExternal)
    {
        var vm = BlockFactory.CreateBlock(BlockType.Image, 0);
        vm.ImagePath = pathOrExternal;
        vm.ImageWidth = 0;
        vm.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
        return vm;
    }

    private static Task<BlockViewModel> SaveClipboardBitmapToNewImageBlockAsync(Bitmap source)
    {
        // Avalonia Bitmap / platform surface must be used on the UI thread; Task.Run breaks Save on Windows.
        var vm = BlockFactory.CreateBlock(BlockType.Image, 0);
        var dir = MnemoAppPaths.GetImagesDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, vm.Id + ".png");
        source.Save(path);
        vm.ImagePath = path;
        vm.ImageWidth = 0;
        vm.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
        return Task.FromResult(vm);
    }

    private static string? NormalizeSingleLineImagePathFromClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.IndexOf('\r') >= 0 || t.IndexOf('\n') >= 0) return null;
        if (t.Length >= 2 &&
            ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\'')))
            t = t[1..^1].Trim();
        return string.IsNullOrWhiteSpace(t) ? null : t;
    }

    /// <summary>Top-level <see cref="Blocks"/> index for the row that contains the focused leaf block.</summary>
    private int GetFocusedBlockIndex()
    {
        if (_focusedBlockIndex >= 0 && _focusedBlockIndex < Blocks.Count)
        {
            var row = Blocks[_focusedBlockIndex];
            if (row.IsFocused)
                return _focusedBlockIndex;
            if (row is TwoColumnBlockViewModel tc
                && (tc.LeftColumnBlocks.Any(b => b.IsFocused) || tc.RightColumnBlocks.Any(b => b.IsFocused)))
                return _focusedBlockIndex;
        }

        var focused = BlockHierarchy.FindFocused(Blocks);
        if (focused != null)
        {
            var top = BlockHierarchy.GetTopLevelIndex(Blocks, focused);
            if (top >= 0)
            {
                _focusedBlockIndex = top;
                return top;
            }
        }
        _focusedBlockIndex = -1;
        return -1;
    }

    #endregion
}
