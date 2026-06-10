using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    internal BlockCollectionManager Collection { get; private set; } = null!;
    internal BlockLifecycleCoordinator Lifecycle { get; private set; } = null!;

    internal BlockDragDropCoordinator BlockReorderDrag { get; private set; } = null!;

    internal void InitializeCoordinators()
    {
        Lifecycle = new BlockLifecycleCoordinator(this);
        Collection = new BlockCollectionManager(this);
        BlockReorderDrag = new BlockDragDropCoordinator(this);
        _blocks.CollectionChanged += Collection.OnBlocksCollectionChanged;
    }

    public void HandleBlockDragOver(Point cursorPosInEditor, BlockViewModel.BlockReorderDragPayload payload) =>
        BlockReorderDrag.HandleBlockDragOver(cursorPosInEditor, payload);

    public int CurrentDropInsertIndex => BlockReorderDrag.CurrentDropInsertIndex;

    public void ClearDropIndicator() => BlockReorderDrag.ClearDropIndicator();

    public void BeginColumnSplitResize() => BlockReorderDrag.BeginColumnSplitResize();

    public void CommitColumnSplitResize() => BlockReorderDrag.CommitColumnSplitResize();

    public bool TryPerformDrop(BlockViewModel draggedBlock) => BlockReorderDrag.TryPerformDrop(draggedBlock);

    public bool TryPerformDrop(BlockViewModel.BlockReorderDragPayload payload) =>
        BlockReorderDrag.TryPerformDrop(payload);

    internal BlockViewModel.BlockReorderDragPayload CreateBlockReorderPayload(
        BlockViewModel handleVm,
        KeyModifiers mods,
        Point? dragStartPointInEditor = null) =>
        BlockReorderDrag.CreateBlockReorderPayload(handleVm, mods, dragStartPointInEditor);

    internal EditableBlock? GetEditableBlockForViewModel(BlockViewModel? vm) =>
        BlockReorderDrag.GetEditableBlockForViewModel(vm);

    internal Rect GetControlBoundsInEditor(Control? c) =>
        BlockReorderDrag.GetControlBoundsInEditor(c);

    internal Rect GetEditableBlockBoundsInEditor(EditableBlock? eb) =>
        BlockReorderDrag.GetEditableBlockBoundsInEditor(eb);

    internal TwoColumnBlockViewModel? ColumnDropTarget
    {
        get => BlockReorderDrag.ColumnDropTarget;
        set => BlockReorderDrag.ColumnDropTarget = value;
    }

    internal bool ColumnDropLeft
    {
        get => BlockReorderDrag.ColumnDropLeft;
        set => BlockReorderDrag.ColumnDropLeft = value;
    }

    internal int ColumnDropInsertIndex
    {
        get => BlockReorderDrag.ColumnDropInsertIndex;
        set => BlockReorderDrag.ColumnDropInsertIndex = value;
    }

    internal int DropInsertIndex
    {
        get => BlockReorderDrag.DropInsertIndex;
        set => BlockReorderDrag.DropInsertIndex = value;
    }

    internal bool TryGetColumnDropInsert(Point cursor, BlockViewModel dragged, out TwoColumnBlockViewModel tc,
        out bool leftColumn, out int insertIndex) =>
        BlockReorderDrag.TryGetColumnDropInsert(cursor, dragged, out tc, out leftColumn, out insertIndex);

    internal int GetInsertIndex(double cursorY) => BlockReorderDrag.GetInsertIndex(cursorY);

    internal void ShowColumnDropLineInOverlay(TwoColumnBlockViewModel tc, bool leftColumn, int insertIndex) =>
        BlockReorderDrag.ShowColumnDropLineInOverlay(tc, leftColumn, insertIndex);

    internal void ShowHorizontalReorderDropLineInOverlay(int insertIndex) =>
        BlockReorderDrag.ShowHorizontalReorderDropLineInOverlay(insertIndex);

    public ObservableCollection<BlockViewModel> Blocks
    {
        get => _blocks;
        set => Collection.Blocks = value;
    }

    internal void RegisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb) =>
        Collection.RegisterRealizedEditableBlock(vm, eb);

    internal void UnregisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb) =>
        Collection.UnregisterRealizedEditableBlock(vm, eb);

    public int RealizedRowCount => Collection.RealizedRowCount;

    public long ComputeContentFingerprint() => Collection.ComputeContentFingerprint();

    internal void SubscribeToBlock(BlockViewModel block) => Collection.SubscribeToBlock(block);

    internal void UnsubscribeFromBlock(BlockViewModel block, bool registerReleasedStoredImagePath = false) =>
        Collection.UnsubscribeFromBlock(block, registerReleasedStoredImagePath);

    internal void TryCollapseSplitAfterDragOut(TwoColumnBlockViewModel? tc) =>
        Lifecycle.TryCollapseSplitAfterDragOut(tc);

    internal void UnwrapTwoColumnPromotingFilledColumn(TwoColumnBlockViewModel tc, bool filledColumnIsLeft) =>
        Lifecycle.UnwrapTwoColumnPromotingFilledColumn(tc, filledColumnIsLeft);

    internal bool RemoveCellFromTwoColumnOrUnwrap(TwoColumnBlockViewModel tc, BlockViewModel block) =>
        Lifecycle.RemoveCellFromTwoColumnOrUnwrap(tc, block);

    internal void NotifyBlocksPropertyChanged() => OnPropertyChanged(nameof(Blocks));

    internal void NotifyBlockRowsChanged() => OnPropertyChanged(nameof(BlockRows));

    internal void RaiseBlocksChangedEvent() => BlocksChanged?.Invoke();
}
