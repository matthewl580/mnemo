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

public partial class BlockEditor : UserControl, INotifyPropertyChanged, IEditorHost
{
    private ObservableCollection<BlockViewModel> _blocks = new();

    /// <summary>Visual rows (one item per stack row; split pairs are one row with two <see cref="EditableBlock"/>s).</summary>
    /// <remarks>
    /// Full rebuilds replace the <see cref="ObservableCollection{T}"/> instance (see <see cref="RebuildBlockRows"/>)
    /// so <see cref="ItemsRepeater"/> receives one binding refresh instead of Clear + N Add notifications.
    /// </remarks>
    public ObservableCollection<BlockRowViewModelBase> BlockRows => _blockRows;

    private ObservableCollection<BlockRowViewModelBase> _blockRows = new();

    /// <summary>Top-level <see cref="Blocks"/> index for the row containing keyboard focus (split row = one index).</summary>
    private int _focusedBlockIndex = -1;

    /// <summary>Leaf block id with keyboard focus (including nested split cells).</summary>
    private string? _focusedBlockId;

    // Drag-box block selection (Mode 2)
    private bool _isBoxSelecting;
    private bool _boxSelectArmed;   // true after pointer-down outside blocks, waiting for threshold
    private Point _boxSelectStart;   // editor space (for hit-test)
    private Point _boxSelectStartInOverlay; // overlay space (for drawing); set when arming
    private Border? _selectionBoxBorder;
    private const double BoxSelectThreshold = 6.0; // pixels to move before box appears
    /// <summary>Anchor for Shift+click range on drag handles; reset when block selection is cleared.</summary>
    private int _blockDragHandleSelectionAnchorIndex = -1;
    private bool _isColumnSplitResizing;
    /// <summary>Horizontal padding on EditorRoot; selection box Margin is relative to the padded content area.</summary>
    private const double EditorContentPaddingX = 32.0;

    private LayoutOverlayPanel? _blockDragGhostOverlay;
    private Border? _blockDragGhostBorder;
    private Vector _blockDragGhostPointerOffset;
    private const double BlockDragGhostOpacity = 0.45;
    private Border? _externalImageDragGhostBorder;
    private static readonly HttpClient ExternalImageHttpClient = new();

    /// <summary>Notes editor: scroll <see cref="ScrollViewer"/> while reordering blocks near viewport top/bottom (same idea as sidebar <see cref="Mnemo.UI.Modules.Notes.Views.DragCoordinator"/>).</summary>
    private DispatcherTimer? _blockDragAutoScrollTimer;
    private double _blockDragAutoScrollDirection;
    private const double BlockDragAutoScrollZone = 40.0;
    private const double BlockDragAutoScrollStep = 9.0;
    private const int BlockDragAutoScrollIntervalMs = 50;
    private readonly record struct FindMatch(string BlockId, int Start, int Length);
    private readonly List<FindMatch> _findMatches = new();
    private int _activeFindMatchIndex = -1;
    private string _findQuery = string.Empty;
    private string _replaceQuery = string.Empty;
    private bool _findPanelVisible;
    private bool _replacePanelExpanded;
    private IOverlayService? _overlayService;
    private string? _findOverlayId;
    private EditorFindPanel? _findPanel;
    private ScrollViewer? _findAnchorScrollHost;
    private EventHandler<SizeChangedEventArgs>? _findAnchorScrollSizeChangedHandler;
    private double? _lastFindOverlayAnchorX;
    private double? _lastFindOverlayAnchorY;
    private bool _isSyncingFindOptionToggles;
    private CaretState? _findCaretBeforeOpen;
    private bool _findNavigatedToMatch;

    /// <summary>Fixed width used for top-right anchor math so overlay position does not feed back into layout.</summary>
    private const double FindOverlayAnchorWidthEstimate = 332.0;

    // Cross-block text selection (Mode 1)
    private bool _isCrossBlockSelecting;
    private bool _crossBlockArmed;  // true after pointer-down inside TextBox, waiting for first move outside
    /// <summary>True while a cross-block text selection drag is actively in progress. Used by EditableBlock to suppress focus side-effects during the drag.</summary>
    public bool IsCrossBlockSelectingActive => _isCrossBlockSelecting;
    /// <summary>True while any pointer-driven selection mode is armed or active. EditableBlock uses this to suppress toolbar checks during drag, which eliminates O(N) scans on every pointer-move-induced selection-change notification.</summary>
    internal bool IsPointerSelecting => _isCrossBlockSelecting || _isBoxSelecting || _crossBlockArmed || _boxSelectArmed;
    private BlockViewModel? _crossBlockAnchorBlock;
    private int _crossBlockAnchorBlockIndex = -1;
    private int _crossBlockAnchorCharIndex;
    private Point _crossBlockStartPoint;
    /// <summary>Hysteresis: last endpoint block index to avoid boundary flicker when dragging across blocks.</summary>
    private int _lastCrossBlockCurrentIndex = -1;
    /// <summary>True while applying inline format to a cross-block selection; prevents focus changes from clearing other blocks' selection.</summary>
    private bool _isApplyingCrossBlockFormat;

    // History / undo-redo
    private IHistoryManager? _history;
    private BlockSnapshot[]? _pendingSnapshot;
    private CaretState? _pendingCaretBefore;
    private bool _isRestoringFromHistory;

    // Text-edit batching (300ms idle flush)
    private DispatcherTimer? _typingBatchTimer;
    private string? _typingBatchBlockId;
    private List<InlineSpan>? _typingBatchRunsBefore;
    private CaretState? _typingBatchCaretBefore;
    private const int TypingBatchIdleMs = 300;

    /// <summary>Reference count of blocks with <see cref="BlockViewModel.IsSelected"/>=true. Lets <see cref="ClearBlockSelection"/> skip a full document walk on every keystroke when no blocks are selected.</summary>
    private int _selectedBlockCount;

    /// <summary>
    /// Cached flat document-order list. Rebuilt lazily on first access after any structural change.
    /// Eliminates the repeated <c>EnumerateInDocumentOrder(Blocks).ToList()</c> allocation that
    /// previously happened on every pointer-move event during selection.
    /// </summary>
    private List<BlockViewModel>? _cachedDocumentOrder;
    private bool _documentOrderDirty = true;
    /// <summary>Reverse index (VM â†’ docIndex) built alongside <see cref="_cachedDocumentOrder"/>. Lets realized-block loops look up positions in O(1) instead of scanning the full list.</summary>
    private Dictionary<BlockViewModel, int>? _cachedDocIndexByVm;

    /// <summary>Pending pointer position captured by <see cref="Editor_PointerMoved"/> but not yet processed. Allows coalescing rapid pointer events so only the latest position is acted on per render frame.</summary>
    private Point _pendingPointerPoint;
    /// <summary>True when a pending pointer-move update has been scheduled on the dispatcher but has not yet run.</summary>
    private bool _pendingPointerUpdateScheduled;
    /// <summary>Pending mode flags for the coalesced pointer update.</summary>
    private bool _pendingPointerIsBox;
    private bool _pendingPointerIsCross;

    /// <summary>
    /// O(1) VM â†’ realized <see cref="EditableBlock"/> map. <see cref="EditableBlock"/> registers in
    /// <c>OnControlLoaded</c> and unregisters in <c>OnControlUnloaded</c>. Replaces the
    /// <c>GetVisualDescendants</c> walks that made every focus change O(NÂ²) over 1500 blocks.
    /// </summary>
    private readonly Dictionary<BlockViewModel, EditableBlock> _realizedBlocksByVm = new();

    /// <summary>Tracked set of blocks whose <see cref="BlockViewModel.Type"/> is <see cref="BlockType.NumberedList"/>.
    /// Maintained via subscribe/unsubscribe + Type-property notifications so <see cref="UpdateListNumbers"/> and
    /// <see cref="ReorderBlocks"/> can skip a full-document walk when there are no numbered lists.</summary>
    private readonly HashSet<BlockViewModel> _numberedListBlocks = new(ReferenceEqualityComparer.Instance);

    internal void RegisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb)
    {
        _realizedBlocksByVm[vm] = eb;
    }

    internal void UnregisterRealizedEditableBlock(BlockViewModel vm, EditableBlock eb)
    {
        if (_realizedBlocksByVm.TryGetValue(vm, out var existing) && ReferenceEquals(existing, eb))
            _realizedBlocksByVm.Remove(vm);
    }

    /// <summary>Diagnostics-only: number of <see cref="EditableBlock"/> rows currently realized by the inner <see cref="ItemsRepeater"/>.</summary>
    public int RealizedRowCount => _realizedBlocksByVm.Count;

    /// <summary>
    /// Cheap structural hash of the live document (no serialization, no allocations beyond the
    /// enumerator). Autosave compares this against the last-saved value to skip the JSON-serialize +
    /// SQLite write entirely when nothing actually changed since the previous save.
    /// </summary>
    /// <remarks>
    /// Covers every persisted field that <see cref="BlockViewModel.ToBlock"/> writes: id, type, order,
    /// flat content, per-span style/value, and the block-type-specific payload fields
    /// (Image/Sketch/Code/Equation/Checklist/NumberedList). Reads <see cref="BlockViewModel.Content"/> which
    /// is O(1) thanks to the cached flat string.
    /// </remarks>
    public long ComputeContentFingerprint()
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        var blockCount = 0;

        unchecked
        {
            long h = 1469598103934665603L; // FNV-1a 64-bit offset basis
            foreach (var b in BlockHierarchy.EnumerateInDocumentOrder(_blocks))
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
                    $"blocks={blockCount} top={Blocks.Count} realized={RealizedRowCount}");
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

            // Spans carry styling that isn't reflected in the flat content (bold/italic/colors/links).
            // GetHashCode on the InlineSpan record types is a structural hash â€” adequate for change detection.
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

    /// <summary>
    /// Set by the owning view to enable document-wide undo/redo.
    /// Cleared on document switch so history does not leak between notes.
    /// </summary>
    public IHistoryManager? History
    {
        get => _history;
        set
        {
            if (ReferenceEquals(_history, value)) return;
            if (_history != null)
                _history.Cleared -= OnHistoryManagerCleared;
            _history = value;
            if (_history != null)
                _history.Cleared += OnHistoryManagerCleared;
        }
    }

    /// <summary>Optional: set from the host view for Mnemo JSON + markdown clipboard.</summary>
    public INoteClipboardPayloadCodec? NoteClipboardCodec { get; set; }

    /// <summary>Optional: set from the host view for multi-format clipboard I/O.</summary>
    public INoteClipboardPlatformService? NoteClipboardService { get; set; }

    /// <summary>Optional: image import when pasting paths / duplicating / hydrating clipboard blocks.</summary>
    public IImageAssetService? ImageAssetService { get; set; }

    /// <summary>Note id whose blocks are being edited; used when inserting a <see cref="BlockType.Page"/> block from the slash menu.</summary>
    public string? HostNoteId { get; set; }

    /// <summary>Resolve a note title by id for <see cref="BlockType.Page"/> button labels.</summary>
    public Func<string, string?>? NoteTitleResolver { get; set; }

    /// <summary>Count direct child notes (<see cref="Note.ParentNoteId"/>) for page block subtitle.</summary>
    public Func<string, int>? ChildPageCountResolver { get; set; }

    /// <summary>Creates a child note under <paramref name="parentNoteId"/>; returns the new note id.</summary>
    public Func<string, Task<string?>>? CreateChildPageUnderNoteAsync { get; set; }

    /// <summary>Optional: persist the current note from the editor immediately (e.g. after inserting a page block).</summary>
    public Func<Task>? FlushPendingNoteSaveAsync { get; set; }

    /// <summary>Fallback label when the referenced note is missing or empty.</summary>
    public string PageBlockMissingTitle { get; set; } = "Missing note";

    /// <summary>Raised when the user activates a page block; host should open <paramref name="noteId"/>.</summary>
    public event Action<string>? OpenReferencedNote;

    public void RequestOpenReferencedNote(string noteId)
    {
        if (string.IsNullOrWhiteSpace(noteId)) return;
        OpenReferencedNote?.Invoke(noteId.Trim());
    }

    /// <summary>Refreshes <see cref="BlockType.Page"/> button titles from <see cref="NoteTitleResolver"/>.</summary>
    public void RefreshPageBlockTitles()
    {
        var missing = PageBlockMissingTitle;
        foreach (var b in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (b.Type == BlockType.Page)
                b.RefreshPageButtonTitle(NoteTitleResolver, missing, ChildPageCountResolver);
        }
    }

    private const string NativeTwoColumnMetaKey = "nativeTwoColumn";

    /// <summary>
    /// Stored image paths the user has replaced or removed from the document; safe to delete once
    /// <see cref="IHistoryManager"/> can no longer undo (see <see cref="IHistoryManager.Cleared"/>).
    /// Paths are removed when they appear again in the document (undo) via <see cref="ReconcileReleasedStoredImagePathsWithDocument"/>.
    /// </summary>
    private readonly HashSet<string> _releasedStoredImagePaths = new(StringComparer.OrdinalIgnoreCase);

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<BlockEditor, bool>(nameof(IsReadOnly), defaultValue: false);

    /// <summary>
    /// When true, block-structural interactions are disabled (drag/drop, slash menu, block add/delete/reorder).
    /// Rich text selection stays available for copy scenarios.
    /// </summary>
    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsReadOnlyProperty)
        {
            foreach (var eb in this.GetVisualDescendants().OfType<EditableBlock>())
                eb.SyncReadOnlyChromeFromBlockEditor();
        }
    }

    public ObservableCollection<BlockViewModel> Blocks
    {
        get => _blocks;
        set
        {
            if (ReferenceEquals(_blocks, value)) return;
            _blocks.CollectionChanged -= OnBlocksCollectionChanged;
            _blocks = value ?? new ObservableCollection<BlockViewModel>();
            _blocks.CollectionChanged += OnBlocksCollectionChanged;
            // Wholesale collection swap does NOT raise CollectionChanged on the new collection
            // (it was constructed pre-populated). The doc-order/index caches still point at the
            // previous note's VMs, so cross-block selection, find, and other realized-block loops
            // would silently fail (TryGetValue returns false for every new VM â†’ index = -1).
            _documentOrderDirty = true;
            _cachedDocumentOrder = null;
            _cachedDocIndexByVm = null;
            OnPropertyChanged();
            RebuildBlockRows();
        }
    }

    private void OnBlocksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _documentOrderDirty = true;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                for (var i = 0; i < e.NewItems!.Count; i++)
                {
                    var block = (BlockViewModel)e.NewItems[i]!;
                    var index = e.NewStartingIndex + i;
                    BlockRows.Insert(index, MakeRow(block, index));
                    for (var j = index + 1; j < BlockRows.Count; j++)
                        BlockRows[j].StartBlockIndex = j;
                }
                break;

            case NotifyCollectionChangedAction.Remove:
                for (var i = 0; i < e.OldItems!.Count; i++)
                    BlockRows.RemoveAt(e.OldStartingIndex);
                for (var j = e.OldStartingIndex; j < BlockRows.Count; j++)
                    BlockRows[j].StartBlockIndex = j;
                break;

            case NotifyCollectionChangedAction.Move:
                BlockRows.Move(e.OldStartingIndex, e.NewStartingIndex);
                var lo = Math.Min(e.OldStartingIndex, e.NewStartingIndex);
                var hi = Math.Max(e.OldStartingIndex, e.NewStartingIndex);
                for (var j = lo; j <= hi; j++)
                    BlockRows[j].StartBlockIndex = j;
                break;

            case NotifyCollectionChangedAction.Reset:
                RebuildBlockRows();
                break;

            default:
                RebuildBlockRows();
                break;
        }
    }

    private void RebuildBlockRows()
    {
        if (Blocks.Count == 0)
        {
            _blockRows = new ObservableCollection<BlockRowViewModelBase>();
            OnPropertyChanged(nameof(BlockRows));
            return;
        }

        var rows = new BlockRowViewModelBase[Blocks.Count];
        for (var i = 0; i < Blocks.Count; i++)
            rows[i] = MakeRow(Blocks[i], i);
        // ctor copies into internal storage without per-item CollectionChanged â€” then we swap the
        // collection reference + INotifyPropertyChanged so ItemsRepeater updates once.
        _blockRows = new ObservableCollection<BlockRowViewModelBase>(rows);
        OnPropertyChanged(nameof(BlockRows));
    }

    private static BlockRowViewModelBase MakeRow(BlockViewModel block, int index) =>
        block is TwoColumnBlockViewModel tc
            ? new SplitBlockRowViewModel(tc, index)
            : new SingleBlockRowViewModel(block, index);

    // TopLevel handlers for global pointer tracking (needed to intercept moves even when TextBox has capture)
    private TopLevel? _topLevel;
    private EventHandler<PointerEventArgs>? _globalPointerMovedHandler;
    private EventHandler<PointerReleasedEventArgs>? _globalPointerReleasedHandler;

    public BlockEditor()
    {
        InitializeComponent();
        _blocks.CollectionChanged += OnBlocksCollectionChanged;
        DataContext = this;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, Editor_DragOver_BlockGhost, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, Editor_Drop, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, Editor_DragLeave, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, Editor_PointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, Editor_PointerPressedBubble, RoutingStrategies.Bubble, handledEventsToo: true);
        // When we capture the pointer (cross-block or box-select), we receive moves/releases on this control
        AddHandler(PointerMovedEvent, Editor_PointerMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, Editor_PointerReleased, RoutingStrategies.Tunnel);
        // Tunnel so we get keys before the focused TextBox (for block-selection Backspace/Copy/Paste)
        AddHandler(KeyDownEvent, Editor_KeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, Editor_KeyDown_Bubble, RoutingStrategies.Bubble);
        Loaded += Editor_Loaded;
        Unloaded += Editor_Unloaded;
        _overlayService = ((App?)Application.Current)?.Services?.GetService<IOverlayService>();

        // Don't add initial block here - let LoadBlocks handle it
    }

    private void Editor_Loaded(object? sender, RoutedEventArgs e)
    {
        ResolveSelectionBoxBorder();

        // Register global pointer handlers on TopLevel so we receive moves/releases
        // even when a child TextBox has captured the pointer for its own text selection.
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel != null)
        {
            _globalPointerMovedHandler = Editor_PointerMoved;
            _globalPointerReleasedHandler = Editor_PointerReleased;
            _topLevel.AddHandler(PointerMovedEvent, _globalPointerMovedHandler, RoutingStrategies.Tunnel);
            _topLevel.AddHandler(PointerReleasedEvent, _globalPointerReleasedHandler, RoutingStrategies.Tunnel);
        }
    }

    private void Editor_Unloaded(object? sender, RoutedEventArgs e)
    {
        CloseFindPanel(clearQuery: false);

        if (_topLevel != null)
        {
            if (_globalPointerMovedHandler != null)
                _topLevel.RemoveHandler(PointerMovedEvent, _globalPointerMovedHandler);
            if (_globalPointerReleasedHandler != null)
                _topLevel.RemoveHandler(PointerReleasedEvent, _globalPointerReleasedHandler);
            _globalPointerMovedHandler = null;
            _globalPointerReleasedHandler = null;
            _topLevel = null;
        }

        if (_history != null)
            _history.Cleared -= OnHistoryManagerCleared;

        EndBlockDragGhost();
    }

    private void ResolveSelectionBoxBorder()
    {
        _selectionBoxBorder ??= this.FindControl<Border>("SelectionBoxBorder");
        if (_selectionBoxBorder == null)
        {
            var grid = this.FindControl<Grid>("EditorRoot");
            _selectionBoxBorder = grid?.GetVisualChildren().OfType<Border>().FirstOrDefault(c => c.Name == "SelectionBoxBorder");
        }
    }

    /// <summary>
    /// During <see cref="DragDrop.DoDragDropAsync"/>, pointer move events are not delivered; <see cref="DragDrop.DragOverEvent"/> is.
    /// </summary>
    private void Editor_DragOver_BlockGhost(object? sender, DragEventArgs e)
    {
        bool isBlockReorderDrag =
            e.DataTransfer.Contains(BlockViewModel.BlockDragDataFormat)
            || e.DataTransfer.Contains(BlockViewModel.BlockReorderDragPayload.Format);
        if (!isBlockReorderDrag)
        {
            if (TryHandleExternalImageDragOver(e))
            {
                e.DragEffects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            HideExternalImageDragGhost();
            e.DragEffects = DragDropEffects.None;
            return;
        }

        HideExternalImageDragGhost();

        if (!TryResolveBlockReorderPayload(e, out var payload) || payload == null)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var pos = e.GetPosition(this);
        if (_blockDragGhostBorder != null && _blockDragGhostOverlay != null)
            UpdateBlockDragGhostFromEditorPoint(pos);

        e.DragEffects = DragDropEffects.Move;
        HandleBlockDragOver(pos, payload);
    }

    private static bool TryResolveBlockReorderPayload(DragEventArgs e, out BlockViewModel.BlockReorderDragPayload? payload)
    {
        if (e.DataTransfer.TryGetValue(BlockViewModel.BlockReorderDragPayload.Format) is BlockViewModel.BlockReorderDragPayload p)
        {
            payload = p;
            return true;
        }

        if (e.DataTransfer.TryGetValue(BlockViewModel.BlockDragDataFormat) is not { } vm)
        {
            payload = null;
            return false;
        }

        payload = new BlockViewModel.BlockReorderDragPayload
        {
            Primary = vm,
            BlocksInDocumentOrder = new[] { vm }
        };
        return true;
    }

    private async void Editor_Drop(object? sender, DragEventArgs e)
    {
        if (e.Handled) return;

        if (!TryResolveBlockReorderPayload(e, out var payload) || payload == null)
        {
            if (await TryDropExternalImageAsync(e).ConfigureAwait(true))
                e.Handled = true;
            return;
        }

        var dropPosInEditor = e.GetPosition(this);
        HandleBlockDragOver(dropPosInEditor, payload);

        try
        {
            TryPerformDrop(payload);
            e.Handled = true;
        }
        catch (Exception)
        {
        }
        finally
        {
            ClearDropIndicator();
        }
    }

    private void Editor_DragLeave(object? sender, DragEventArgs e)
    {
        // DragLeave bubbles on every child-to-child transition inside the editor.
        // Only clear the indicator when the cursor has actually left the editor bounds.
        var pos = e.GetPosition(this);
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (!bounds.Contains(pos))
        {
            ClearDropIndicator();
            HideExternalImageDragGhost();
        }
    }

    private bool TryHandleExternalImageDragOver(DragEventArgs e)
    {
        if (!IsExternalImageDragData(e.DataTransfer))
            return false;

        var cursor = e.GetPosition(this);
        ShowExternalImageDragGhost(cursor);
        if (TryGetUnassignedImageBlockAtPoint(cursor) != null)
        {
            ClearDropIndicator();
            return true;
        }

        var probeImage = BlockFactory.CreateBlock(BlockType.Image, 0);
        if (TryGetColumnDropInsert(cursor, probeImage, out var tc, out var leftColumn, out var insertIndex))
        {
            if (!(ReferenceEquals(_columnDropTarget, tc)
                && _columnDropLeft == leftColumn
                && _columnDropInsertIndex == insertIndex
                && this.FindControl<Border>("BlockReorderDropLineOverlay") is { IsVisible: true }))
            {
                ClearDropIndicator();
                _columnDropTarget = tc;
                _columnDropLeft = leftColumn;
                _columnDropInsertIndex = insertIndex;
                _currentDropInsertIndex = -1;
                ShowColumnDropLineInOverlay(tc, leftColumn, insertIndex);
            }

            return true;
        }

        var topInsert = GetInsertIndex(cursor.Y);
        if (topInsert < 0)
            return false;

        if (_currentDropInsertIndex != topInsert || _columnDropTarget != null)
        {
            ClearDropIndicator();
            _currentDropInsertIndex = topInsert;
            ShowHorizontalReorderDropLineInOverlay(topInsert);
        }

        return true;
    }

    private async Task<bool> TryDropExternalImageAsync(DragEventArgs e)
    {
        if (!IsExternalImageDragData(e.DataTransfer))
            return false;

        if (e.DataTransfer is not IAsyncDataTransfer asyncTransfer)
        {
            return false;
        }

        var droppedImage = await TryCreateImageBlockFromDropDataAsync(asyncTransfer).ConfigureAwait(true);
        if (droppedImage == null)
            return false;

        var cursor = e.GetPosition(this);
        try
        {
            BeginStructuralChange();

            var unassignedTarget = TryGetUnassignedImageBlockAtPoint(cursor);
            if (unassignedTarget != null)
            {
                var importedPath = await ImportImagePathForTargetAsync(droppedImage.ImagePath, unassignedTarget.Id).ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(importedPath))
                    unassignedTarget.ImagePath = importedPath;
                else
                    unassignedTarget.ImagePath = droppedImage.ImagePath;

                unassignedTarget.ImageWidth = 0;
                unassignedTarget.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
                ClearBlockSelection();
                CommitStructuralChange("Drop image");
                BlocksChanged?.Invoke();
                Dispatcher.UIThread.Post(() => unassignedTarget.IsFocused = true, DispatcherPriority.Input);
                return true;
            }

            await HydratePastedImageBlocksAsync(new BlockViewModel[] { droppedImage }).ConfigureAwait(true);

            if (_columnDropTarget != null && _columnDropInsertIndex >= 0)
            {
                var column = _columnDropLeft ? _columnDropTarget.LeftColumnBlocks : _columnDropTarget.RightColumnBlocks;
                var insertAt = Math.Clamp(_columnDropInsertIndex, 0, column.Count);
                BlockHierarchy.WireChildOwnership(_columnDropTarget, droppedImage, _columnDropLeft);
                SubscribeToBlock(droppedImage);
                column.Insert(insertAt, droppedImage);
            }
            else
            {
                var insertIndex = _currentDropInsertIndex;
                if (insertIndex < 0 || insertIndex > Blocks.Count)
                {
                    insertIndex = GetInsertIndex(cursor.Y);
                    if (insertIndex < 0)
                        insertIndex = Blocks.Count;
                }

                insertIndex = Math.Clamp(insertIndex, 0, Blocks.Count);
                SubscribeToBlock(droppedImage);
                Blocks.Insert(insertIndex, droppedImage);
                droppedImage.Order = insertIndex;
            }

            ReorderBlocks();
            ClearBlockSelection();
            CommitStructuralChange("Drop image");
            BlocksChanged?.Invoke();
            Dispatcher.UIThread.Post(() => droppedImage.IsFocused = true, DispatcherPriority.Input);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            ClearDropIndicator();
            HideExternalImageDragGhost();
        }
    }

    private async Task<BlockViewModel?> TryCreateImageBlockFromDropDataAsync(IAsyncDataTransfer data)
    {
        try
        {
            var files = await data.TryGetFilesAsync().ConfigureAwait(true);
            if (files != null)
            {
                foreach (var f in files)
                {
                    var p = f.TryGetLocalPath();
                    if (string.IsNullOrWhiteSpace(p) || !File.Exists(p)) continue;
                    if (!ClipboardImageExtensions.Contains(Path.GetExtension(p))) continue;
                    return CreateImageBlockStubForPaste(p);
                }
            }
        }
        catch
        {
            // fall through to bitmap/text probes
        }

        var bitmap = await data.TryGetBitmapAsync().ConfigureAwait(true);
        if (bitmap != null)
        {
            try
            {
                return await SaveClipboardBitmapToNewImageBlockAsync(bitmap).ConfigureAwait(true);
            }
            finally
            {
                bitmap.Dispose();
            }
        }

        if (data is IDataTransfer syncTransfer
            && syncTransfer.TryGetValue(DataFormat.Text) is string text)
        {
            var candidate = NormalizeSingleLineImagePathFromClipboard(text);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                && ClipboardImageExtensions.Contains(Path.GetExtension(candidate)))
            {
                return CreateImageBlockStubForPaste(candidate);
            }

            var url = NormalizeSingleLineImageUrlFromDragText(text);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return await DownloadExternalImageToNewImageBlockAsync(url).ConfigureAwait(true);
            }
        }

        try
        {
            var asyncText = await data.TryGetTextAsync().ConfigureAwait(true);
            var candidate = NormalizeSingleLineImagePathFromClipboard(asyncText);
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)
                && ClipboardImageExtensions.Contains(Path.GetExtension(candidate)))
            {
                return CreateImageBlockStubForPaste(candidate);
            }

            var url = NormalizeSingleLineImageUrlFromDragText(asyncText);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return await DownloadExternalImageToNewImageBlockAsync(url).ConfigureAwait(true);
            }
        }
        catch
        {
            // Some sources expose text only through sync formats; ignore.
        }

        return null;
    }

    private static bool IsExternalImageDragData(IDataTransfer data)
    {
        if (data.Contains(DataFormat.File))
            return true;

        if (data.TryGetValue(DataFormat.Text) is string text)
        {
            var trimmed = text.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return true;
            if (NormalizeSingleLineImagePathFromClipboard(trimmed) != null)
                return true;
        }

        // Browser drags frequently expose URL text only via async transfer APIs.
        return data is IAsyncDataTransfer;
    }

    private BlockViewModel? TryGetUnassignedImageBlockAtPoint(Point cursorPosInEditor)
    {
        foreach (var vm in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (vm.Type != BlockType.Image)
                continue;
            if (!string.IsNullOrWhiteSpace(vm.ImagePath))
                continue;

            var bounds = GetEditableBlockBoundsInEditor(GetEditableBlockForViewModel(vm));
            if (bounds.Width <= 0 || bounds.Height <= 0)
                continue;
            if (bounds.Contains(cursorPosInEditor))
                return vm;
        }

        return null;
    }

    private async Task<string?> ImportImagePathForTargetAsync(string? sourcePath, string targetBlockId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return sourcePath;

        var svc = ResolveImageAssetService();
        if (svc == null)
            return sourcePath;

        try
        {
            var imported = await svc.ImportAndCopyAsync(sourcePath, targetBlockId).ConfigureAwait(true);
            if (imported.IsSuccess && !string.IsNullOrWhiteSpace(imported.Value))
                return imported.Value;
        }
        catch
        {
            // Keep original path when import fails (e.g. locked file).
        }

        return sourcePath;
    }

    private void ShowExternalImageDragGhost(Point cursorPosInEditor)
    {
        var overlay = this.FindControl<LayoutOverlayPanel>("BlockDragGhostOverlay");
        if (overlay == null)
            return;

        if (_externalImageDragGhostBorder == null)
        {
            _externalImageDragGhostBorder = new Border
            {
                Width = 132,
                Height = 34,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(230, 33, 33, 33)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "Drop image",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 12
                }
            };

            overlay.Children.Add(_externalImageDragGhostBorder);
        }

        Canvas.SetLeft(_externalImageDragGhostBorder, cursorPosInEditor.X + 14);
        Canvas.SetTop(_externalImageDragGhostBorder, cursorPosInEditor.Y + 14);
        _externalImageDragGhostBorder.IsVisible = true;
        overlay.InvalidateArrange();
    }

    private void HideExternalImageDragGhost()
    {
        if (_externalImageDragGhostBorder == null)
            return;
        _externalImageDragGhostBorder.IsVisible = false;
    }

    private static string? NormalizeSingleLineImageUrlFromDragText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var candidate = text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("#", StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return null;

        return uri.ToString();
    }

    private static async Task<BlockViewModel?> DownloadExternalImageToNewImageBlockAsync(string imageUrl)
    {
        try
        {
            using var response = await ExternalImageHttpClient.GetAsync(imageUrl).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
                return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType)
                && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            if (bytes.Length == 0)
                return null;

            // Validate formats that Avalonia reliably decodes before persisting.
            if (mediaType is null
                || mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/bmp", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("image/tiff", StringComparison.OrdinalIgnoreCase))
            {
                await using var probe = new MemoryStream(bytes, writable: false);
                try
                {
                    using var _ = new Bitmap(probe);
                }
                catch
                {
                    return null;
                }
            }

            var vm = BlockFactory.CreateBlock(BlockType.Image, 0);
            var dir = MnemoAppPaths.GetImagesDirectory();
            Directory.CreateDirectory(dir);

            var ext = GuessImageFileExtension(response.Content.Headers.ContentType?.MediaType, imageUrl);
            var path = Path.Combine(dir, vm.Id + ext);
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(true);

            vm.ImagePath = path;
            vm.ImageWidth = 0;
            vm.SetSpans(new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
            return vm;
        }
        catch
        {
            return null;
        }
    }

    private static string GuessImageFileExtension(string? mediaType, string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            if (mediaType.Contains("png", StringComparison.OrdinalIgnoreCase)) return ".png";
            if (mediaType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            if (mediaType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
            if (mediaType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (mediaType.Contains("bmp", StringComparison.OrdinalIgnoreCase)) return ".bmp";
            if (mediaType.Contains("tiff", StringComparison.OrdinalIgnoreCase)) return ".tiff";
        }

        var ext = Path.GetExtension(sourceUrl);
        return ClipboardImageExtensions.Contains(ext) ? ext : ".png";
    }

    public void LoadBlocks(Block[] blocks)
    {
        var perf = EditorPerfDiagnostics.Resolve();
        using var loadScope = EditorPerfDiagnostics.Measure(perf, "loadBlocks", $"{blocks?.Length ?? 0} top-level");
        var phaseStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        void MarkPhase(string phase, string? detail = null)
        {
            if (perf is not { IsEnabled: true } || phaseStart == 0)
                return;

            var ms = EditorPerfDiagnostics.ElapsedMs(phaseStart);
            perf.RecordTiming("NotesEditor", $"loadBlocks.{phase}", ms, detail);
            phaseStart = Stopwatch.GetTimestamp();
        }

        var subStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        FlushTypingBatch();
        EditorPerfDiagnostics.RecordPhase(perf, subStart, "loadBlocks.reset.flushTypingBatch");

        subStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        _pendingSnapshot = null;
        _pendingCaretBefore = null;
        _history?.Clear();
        EditorPerfDiagnostics.RecordPhase(perf, subStart, "loadBlocks.reset.historyClear");

        _selectedBlockCount = 0;
        // Defensive reset: any cross-block / box-select state must not leak across notes.
        // Anchor VMs from the previous note will be unsubscribed below; leaving them set here
        // would let a stale pointer-move event poke a freed-up VM.
        _isCrossBlockSelecting = false;
        _crossBlockArmed = false;
        _crossBlockAnchorBlock = null;
        _crossBlockAnchorBlockIndex = -1;
        _lastCrossBlockCurrentIndex = -1;
        _isBoxSelecting = false;
        _boxSelectArmed = false;
        _pendingPointerUpdateScheduled = false;
        MarkPhase("reset");

        // Cache may hold entries for VMs from the previous document. Container Unloaded /
        // OnDataContextChanged eventually evicts them, but the timing isn't guaranteed before
        // the new document's first lookup, so wipe up front.
        _realizedBlocksByVm.Clear();
        _numberedListBlocks.Clear();

        // Unsubscribe from old blocks (do not register released paths â€” note switch / persistence owns asset lifetime).
        foreach (var block in Blocks)
            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: false);
        MarkPhase("unsubscribeOld", $"{Blocks.Count} previous top-level");

        // Create new collection to ensure proper UI notification
        var newBlocks = new ObservableCollection<BlockViewModel>();
        
        if (blocks == null || blocks.Length == 0)
        {
            var defaultBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
            SubscribeToBlock(defaultBlock);
            newBlocks.Add(defaultBlock);
            MarkPhase("buildDefault");
        }
        else
        {
            var expanded = ColumnPairHelper.ExpandLegacyTwoColumnBlocks(blocks.Where(b => b != null));
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in expanded.OrderBy(b => b.Order))
            {
                if (!string.IsNullOrEmpty(block.Id) && !seenIds.Add(block.Id))
                    continue;

                BlockViewModel viewModel;
                if (ColumnPairHelper.IsNestedTwoColumnBlock(block))
                {
                    var tc = new TwoColumnBlockViewModel(block);
                    SubscribeToBlock(tc);
                    viewModel = tc;
                }
                else
                {
                    viewModel = new BlockViewModel(block);
                    SubscribeToBlock(viewModel);
                }
                newBlocks.Add(viewModel);
            }

            ColumnPairHelper.MergeConsecutiveColumnPairs(newBlocks);
            MarkPhase("buildViewModels", $"{newBlocks.Count} top-level rows");
            
            // If no valid blocks were added, add a default one
            if (newBlocks.Count == 0)
            {
                var defaultBlock = BlockFactory.CreateBlock(BlockType.Text, 0);
                SubscribeToBlock(defaultBlock);
                newBlocks.Add(defaultBlock);
            }
        }
        
        // Replace entire collection to trigger UI update
        _focusedBlockIndex = -1;
        _focusedBlockId = null;
        Blocks = newBlocks;
        MarkPhase("assignBlocks", $"{newBlocks.Count} top-level");
        
        // Update list numbers after loading
        UpdateListNumbers();
        MarkPhase("updateListNumbers");

        RefreshPageBlockTitles();
        MarkPhase("refreshPageTitles");

        ReconcileReleasedStoredImagePathsWithDocument();
        MarkPhase("reconcileImages");

        // Focus the first block after UI updates to make it immediately editable
        if (newBlocks.Count > 0)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(
                () => newBlocks[0].IsFocused = true, 
                Avalonia.Threading.DispatcherPriority.Loaded);
        }
        MarkPhase("postFocus");
    }

    public Block[] GetBlocks()
    {
        var perf = EditorPerfDiagnostics.Resolve();
        using var scope = EditorPerfDiagnostics.Measure(perf, "getBlocks", $"{Blocks.Count} top-level");

        // Update order before returning
        for (int i = 0; i < Blocks.Count; i++)
        {
            Blocks[i].Order = i;
        }

        return Blocks.Select(b => b.ToBlock()).ToArray();
    }

    public void AddBlock(BlockType type, int? position = null, string? initialContent = null)
    {
        var order = position ?? Blocks.Count;
        var block = BlockFactory.CreateBlock(type, order);
        if (initialContent != null)
            block.Content = initialContent;
        SubscribeToBlock(block);

        if (position.HasValue && position.Value < Blocks.Count)
        {
            Blocks.Insert(position.Value, block);
        }
        else
        {
            Blocks.Add(block);
        }
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
        var index = Blocks.IndexOf(block);
        if (index < 0) return;

        var runs = block.Spans;
        var selectedText = Core.Formatting.InlineSpanFormatApplier.Flatten(
            Core.Formatting.InlineSpanFormatApplier.SliceRuns(runs, selStart, selEnd));
        var latex = Core.Formatting.EquationLatexNormalizer.Normalize(selectedText);

        var beforeRuns = Core.Formatting.InlineSpanFormatApplier.SliceRuns(runs, 0, selStart);
        var afterRuns = Core.Formatting.InlineSpanFormatApplier.SliceRuns(runs, selEnd,
            Core.Formatting.InlineSpanFormatApplier.Flatten(runs).Length);

        BeginStructuralChange();

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        Blocks.RemoveAt(index);

        int insertAt = index;

        if (beforeRuns.Count > 0 && !string.IsNullOrEmpty(Core.Formatting.InlineSpanFormatApplier.Flatten(beforeRuns)))
        {
            var beforeVm = BlockFactory.CreateBlock(block.Type, 0);
            beforeVm.CommitSpansFromEditor(beforeRuns);
            SubscribeToBlock(beforeVm);
            Blocks.Insert(insertAt++, beforeVm);
        }

        var eqVm = BlockFactory.CreateBlock(BlockType.Equation, 0);
        eqVm.EquationLatex = latex;
        SubscribeToBlock(eqVm);
        Blocks.Insert(insertAt++, eqVm);

        if (afterRuns.Count > 0 && !string.IsNullOrEmpty(Core.Formatting.InlineSpanFormatApplier.Flatten(afterRuns)))
        {
            var afterVm = BlockFactory.CreateBlock(block.Type, 0);
            afterVm.CommitSpansFromEditor(afterRuns);
            SubscribeToBlock(afterVm);
            Blocks.Insert(insertAt++, afterVm);
        }

        ReorderBlocks();
        CommitStructuralChange("Split to equation");
        NotifyBlocksChanged();

        Dispatcher.UIThread.Post(() => eqVm.IsFocused = true, DispatcherPriority.Render);
    }

    private void SubscribeToBlock(BlockViewModel block)
    {
        if (block.Type == BlockType.NumberedList)
            _numberedListBlocks.Add(block);

        block.ContentChanged += OnBlockContentChanged;
        block.PropertyChanged += OnBlockPropertyChanged;
        block.DeleteRequested += OnBlockDeleteRequested;
        block.DuplicateBlockRequested += OnDuplicateBlockRequested;
        block.NewBlockRequested += OnNewBlockRequested;
        block.NewBlockOfTypeRequested += OnNewBlockOfTypeRequested;
        block.NewBlockAboveRequested += OnNewBlockAboveRequested;
        block.DeleteAndFocusAboveRequested += OnDeleteAndFocusAboveRequested;
        block.FocusPreviousRequested += OnFocusPreviousRequested;
        block.FocusNextRequested += OnFocusNextRequested;
        block.MergeWithPreviousRequested += OnMergeWithPreviousRequested;
        block.ExitSplitBelowRequested += OnExitSplitBelowRequested;
        block.StructuralChangeStarting += OnStructuralChangeStarting;
        block.StructuralChangeCompleted += OnStructuralChangeCompleted;
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

    private void OnTwoColumnColumnChildrenChanged(TwoColumnBlockViewModel tc, bool leftColumn, NotifyCollectionChangedEventArgs e)
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
        UpdateListNumbers();
        NotifyBlocksChanged();
    }

    /// <param name="registerReleasedStoredImagePath">
    /// True when the block is permanently removed or replaced (delete, merge, paste replace, etc.).
    /// False when detaching for reorder, column moves, undo/redo restore, or reparenting.
    /// </param>
    private void UnsubscribeFromBlock(BlockViewModel block, bool registerReleasedStoredImagePath = false)
    {
        if (registerReleasedStoredImagePath && block.Type == BlockType.Image)
            RegisterReleasedStoredImagePathCore(block.ImagePath);

        _numberedListBlocks.Remove(block);

        block.ContentChanged -= OnBlockContentChanged;
        block.PropertyChanged -= OnBlockPropertyChanged;
        block.DeleteRequested -= OnBlockDeleteRequested;
        block.DuplicateBlockRequested -= OnDuplicateBlockRequested;
        block.NewBlockRequested -= OnNewBlockRequested;
        block.NewBlockOfTypeRequested -= OnNewBlockOfTypeRequested;
        block.NewBlockAboveRequested -= OnNewBlockAboveRequested;
        block.DeleteAndFocusAboveRequested -= OnDeleteAndFocusAboveRequested;
        block.FocusPreviousRequested -= OnFocusPreviousRequested;
        block.FocusNextRequested -= OnFocusNextRequested;
        block.MergeWithPreviousRequested -= OnMergeWithPreviousRequested;
        block.ExitSplitBelowRequested -= OnExitSplitBelowRequested;
        block.StructuralChangeStarting -= OnStructuralChangeStarting;
        block.StructuralChangeCompleted -= OnStructuralChangeCompleted;
        if (block is TwoColumnBlockViewModel tc)
        {
            tc.ColumnChildrenChanged -= OnTwoColumnColumnChildrenChanged;
            foreach (var c in tc.LeftColumnBlocks.ToList())
                UnsubscribeFromBlock(c, registerReleasedStoredImagePath);
            foreach (var c in tc.RightColumnBlocks.ToList())
                UnsubscribeFromBlock(c, registerReleasedStoredImagePath);
        }
    }

    private void OnStructuralChangeStarting()
    {
        BeginStructuralChange();
    }

    private void OnStructuralChangeCompleted(string description)
    {
        CommitStructuralChange(description);
    }

    private void OnBlockPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BlockViewModel.Type))
        {
            // Keep the numbered-list set in sync. BlockViewModel doesn't expose the prior type,
            // but the set semantics (Add/Remove are idempotent) tolerate that.
            if (sender is BlockViewModel typedBlock)
            {
                if (typedBlock.Type == BlockType.NumberedList)
                    _numberedListBlocks.Add(typedBlock);
                else
                    _numberedListBlocks.Remove(typedBlock);
            }

            UpdateListNumbers();
            if (!_isRestoringFromHistory && _pendingSnapshot != null)
            {
                CommitStructuralChange("Change block type");
            }
            return;
        }

        if (e.PropertyName == nameof(BlockViewModel.IsChecked))
        {
            if (!_isRestoringFromHistory && _pendingSnapshot != null)
            {
                CommitStructuralChange("Toggle checklist");
            }
            return;
        }

        if (e.PropertyName == nameof(BlockViewModel.IsSelected))
        {
            if (sender is BlockViewModel sb)
            {
                if (sb.IsSelected) _selectedBlockCount++;
                else if (_selectedBlockCount > 0) _selectedBlockCount--;
            }
            return;
        }

        if (e.PropertyName != nameof(BlockViewModel.IsFocused)) return;
        if (sender is not BlockViewModel block) return;

        if (block.IsFocused)
        {
            var topIdx = BlockHierarchy.GetTopLevelIndex(Blocks, block);
            // Posted focus (e.g. new block after Enter) can run before the previous editor's LostFocus, so two
            // VMs may briefly have IsFocused. Clear only the last known owner â€” O(1); avoids NÃ— EditableBlock
            // teardown (slash menu, toolbar posts) when the document has many blocks. RichTextEditor still
            // gates watermark paint on real keyboard/pointer focus.
            if (!string.IsNullOrEmpty(_focusedBlockId) && _focusedBlockId != block.Id)
            {
                var prev = BlockHierarchy.FindById(Blocks, _focusedBlockId);
                if (prev?.IsFocused == true)
                    prev.IsFocused = false;
            }

            _focusedBlockId = block.Id;
            _focusedBlockIndex = topIdx;

            // During cross-block text selection the editor controls which blocks have text
            // selection. Clearing it here (triggered by IsFocused changes on each block we
            // update) would fight with UpdateCrossBlockSelection and cause flicker.
            if (_isCrossBlockSelecting || _crossBlockArmed) return;
            // When applying format to cross-block selection we focus each block in turn; don't clear other blocks' selection.
            if (_isApplyingCrossBlockFormat) return;

            ClearBlockSelection();
            ClearTextSelectionInAllBlocksExcept(block);
        }
        else if (!string.IsNullOrEmpty(_focusedBlockId) && _focusedBlockId == block.Id)
        {
            _focusedBlockId = null;
            _focusedBlockIndex = -1;
        }
    }

    private void OnBlockContentChanged(BlockViewModel block)
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        ClearBlockSelection();
        var prev = block.PreviousContent;
        var prevRuns = block.PreviousSpans;
        block.PreviousContent = null;
        block.PreviousSpans = null;

        if (!_isRestoringFromHistory && _pendingSnapshot == null)
        {
            if (prev != null || prevRuns != null)
            {
                TrackTypingEdit(block, prev ?? block.Content, prevRuns);
            }
            else
            {
            }
        }
        else
        {
        }
        if (_findPanelVisible)
        {
            var findStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
            RefreshFindMatchesAndHighlights();
            EditorPerfDiagnostics.RecordPhase(perf, findStart, "contentChanged.findRefresh");
        }

        var notifyStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        BlocksChanged?.Invoke();
        EditorPerfDiagnostics.RecordPhase(perf, notifyStart, "contentChanged.blocksChanged");

        if (perfStart != 0)
        {
            var ms = EditorPerfDiagnostics.ElapsedMs(perfStart);
            EditorPerfDiagnostics.ReportContentChange(ms);
            EditorPerfDiagnostics.RecordIfSlow(perf, "contentChanged", ms, $"top={Blocks.Count} realized={RealizedRowCount}");
        }
    }

    private void OnBlockDeleteRequested(BlockViewModel block)
    {
        DeleteBlock(block);
    }

    private void OnDeleteAndFocusAboveRequested(BlockViewModel block)
    {
        DeleteBlock(block);
    }

    private void OnMergeWithPreviousRequested(BlockViewModel block)
    {
        var previousBlock = BlockHierarchy.FindPreviousInDocumentOrder(Blocks, block);
        if (previousBlock == null) return;

        if (_pendingSnapshot == null)
            BeginStructuralChange();
        var insertionPoint = previousBlock.Content?.Length ?? 0;
        var suffix = BlockEditorContentPolicy.MergeSuffixFromFollowingBlock(block.Content);
        previousBlock.Content = (previousBlock.Content ?? string.Empty) + suffix;

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            col.Remove(block);
            BlockHierarchy.ClearChildOwnership(block);
            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            if (col.Count == 0)
            {
                var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
                BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
                col.Add(ph);
                SubscribeToBlock(ph);
            }
        }
        else
        {
            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            Blocks.Remove(block);
        }

        ReorderBlocks();
        CommitStructuralChange("Merge blocks");
        BlocksChanged?.Invoke();

        var caretTarget = insertionPoint;
        Dispatcher.UIThread.Post(
            () =>
            {
                previousBlock.PendingCaretIndex = caretTarget;
                previousBlock.IsFocused = true;
            },
            DispatcherPriority.Input);
    }

    private void OnExitSplitBelowRequested(BlockViewModel block, string? followingText)
    {
        if (block.OwnerTwoColumn is not TwoColumnBlockViewModel tc) return;
        var topIdx = Blocks.IndexOf(tc);
        if (topIdx < 0) return;

        if (_pendingSnapshot == null)
            BeginStructuralChange();

        var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var bodyEmpty = BlockEditorContentPolicy.IsVisuallyEmpty(block.Content ?? "");
        if (bodyEmpty && col.Contains(block))
        {
            col.Remove(block);
            BlockHierarchy.ClearChildOwnership(block);
            UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
            if (col.Count == 0)
            {
                var ph = BlockFactory.CreateBlock(BlockType.Text, 0);
                BlockHierarchy.WireChildOwnership(tc, ph, block.IsLeftColumn);
                col.Add(ph);
                SubscribeToBlock(ph);
            }
        }

        var nb = BlockFactory.CreateBlock(BlockType.Text, 0);
        if (!string.IsNullOrEmpty(followingText))
            nb.Content = followingText;
        SubscribeToBlock(nb);
        Blocks.Insert(topIdx + 1, nb);
        ReorderBlocks();
        CommitStructuralChange("Exit split");
        BlocksChanged?.Invoke();
        Dispatcher.UIThread.Post(() => nb.IsFocused = true, DispatcherPriority.Input);
    }

    private void DeleteBlock(BlockViewModel block)
    {
        if (_pendingSnapshot == null)
            BeginStructuralChange();

        if (block.OwnerTwoColumn is TwoColumnBlockViewModel tc)
        {
            var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
            var ci = col.IndexOf(block);
            if (ci < 0) return;

            if (ShouldUnwrapTwoColumnBecauseAllCellsEmpty(tc, block))
            {
                var topIdx = Blocks.IndexOf(tc);
                if (topIdx < 0) return;
                UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
                Blocks.RemoveAt(topIdx);
                var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
                SubscribeToBlock(placeholder);
                Blocks.Insert(topIdx, placeholder);
                ReorderBlocks();
                CommitStructuralChange("Unwrap split");
                BlocksChanged?.Invoke();
                Dispatcher.UIThread.Post(() => placeholder.IsFocused = true, DispatcherPriority.Input);
                return;
            }

            if (ShouldUnwrapTwoColumnBecauseOneColumnEmptyOtherHasContent(tc, block))
            {
                var filledColumnIsLeft = !ColumnIsEntirelyVisuallyEmpty(tc, true) &&
                    ColumnIsEntirelyVisuallyEmpty(tc, false);
                UnwrapTwoColumnPromotingFilledColumn(tc, filledColumnIsLeft);
                CommitStructuralChange("Unwrap split");
                BlocksChanged?.Invoke();
                return;
            }

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
                CommitStructuralChange("Delete block");
                BlocksChanged?.Invoke();
                Dispatcher.UIThread.Post(() => placeholder.IsFocused = true, DispatcherPriority.Input);
                return;
            }

            if (RemoveCellFromTwoColumnOrUnwrap(tc, block))
            {
                CommitStructuralChange("Unwrap split");
                BlocksChanged?.Invoke();
                return;
            }

            CommitStructuralChange("Delete block");
            BlocksChanged?.Invoke();
            var focusIdx = Math.Max(0, Math.Min(ci, col.Count - 1));
            Dispatcher.UIThread.Post(() => col[focusIdx].IsFocused = true, DispatcherPriority.Input);
            return;
        }

        var index = Blocks.IndexOf(block);
        if (index == -1) return;

        if (Blocks.Count == 1)
        {
            if (block.Type == BlockType.Image)
                RegisterReleasedStoredImagePathCore(block.ImagePath);
            block.Content = string.Empty;
            block.Type = BlockType.Text;
            block.IsFocused = true;
            UpdateListNumbers();
            CommitStructuralChange("Clear block");
            return;
        }

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
        Blocks.Remove(block);

        var targetIndex = index > 0 ? index - 1 : 0;
        if (Blocks.Count > targetIndex)
        {
            Dispatcher.UIThread.Post(
                () => Blocks[targetIndex].IsFocused = true, 
                DispatcherPriority.Input);
        }

        ReorderBlocks();
        CommitStructuralChange("Delete block");
        BlocksChanged?.Invoke();
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

    /// <summary>
    /// When every cell in the split is visually empty (including <paramref name="block"/>), backspace
    /// removes the whole TwoColumn and restores a single top-level paragraph.
    /// </summary>
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

    /// <summary>
    /// One column is only empty paragraphs; the other has real content â€” backspace in the empty column
    /// removes the split and promotes the filled column to top-level blocks.
    /// </summary>
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

    private void UnwrapTwoColumnPromotingFilledColumn(TwoColumnBlockViewModel tc, bool filledColumnIsLeft)
    {
        var topIdx = Blocks.IndexOf(tc);
        if (topIdx < 0) return;

        var filledCol = filledColumnIsLeft ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var emptyCol = filledColumnIsLeft ? tc.RightColumnBlocks : tc.LeftColumnBlocks;

        var promoted = filledCol.ToList();
        foreach (var b in emptyCol.ToList())
        {
            UnsubscribeFromBlock(b, registerReleasedStoredImagePath: true);
            emptyCol.Remove(b);
        }
        foreach (var b in promoted)
        {
            UnsubscribeFromBlock(b, registerReleasedStoredImagePath: false);
            filledCol.Remove(b);
            BlockHierarchy.ClearChildOwnership(b);
        }

        UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: false);
        Blocks.RemoveAt(topIdx);

        int insertAt = topIdx;
        foreach (var b in promoted)
        {
            SubscribeToBlock(b);
            Blocks.Insert(insertAt++, b);
        }

        ReorderBlocks();
        var focus = promoted.Count > 0 ? promoted[0] : null;
        if (focus != null)
            Dispatcher.UIThread.Post(() => focus.IsFocused = true, DispatcherPriority.Input);
    }

    /// <summary>Removes a cell from a split. If that column becomes empty and the other column has blocks, unwraps to top-level.
    /// Returns true when the split was removed via unwrap.</summary>
    private bool RemoveCellFromTwoColumnOrUnwrap(TwoColumnBlockViewModel tc, BlockViewModel block)
    {
        var col = block.IsLeftColumn ? tc.LeftColumnBlocks : tc.RightColumnBlocks;
        var ci = col.IndexOf(block);
        if (ci < 0) return false;

        UnsubscribeFromBlock(block, registerReleasedStoredImagePath: true);
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
            SubscribeToBlock(ph);
        }

        ReorderBlocks();
        return false;
    }

    /// <summary>
    /// After the last non-empty block is dragged out, only empty placeholders remain â€” replace the split with one empty paragraph.
    /// </summary>
    private void TryCollapseSplitToSingleEmptyIfAllLeavesEmpty(TwoColumnBlockViewModel tc)
    {
        if (Blocks.IndexOf(tc) < 0) return;
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
        var topIdx = Blocks.IndexOf(tc);
        if (topIdx < 0) return;
        UnsubscribeFromBlock(tc, registerReleasedStoredImagePath: true);
        Blocks.RemoveAt(topIdx);
        var placeholder = BlockFactory.CreateBlock(BlockType.Text, 0);
        SubscribeToBlock(placeholder);
        Blocks.Insert(topIdx, placeholder);
        ReorderBlocks();
    }

    /// <summary>
    /// Run after a block was detached to top-level so the split row can collapse (only empty cells left,
    /// or one column entirely empty and the other still has content â€” unwrap to a single column stack).
    /// Must run after <see cref="Blocks.Insert"/> for the dragged block so indices and subscriptions are consistent.
    /// </summary>
    private void TryCollapseSplitAfterDragOut(TwoColumnBlockViewModel? tc)
    {
        if (tc == null || Blocks.IndexOf(tc) < 0) return;
        TryCollapseSplitToSingleEmptyIfAllLeavesEmpty(tc);
        if (Blocks.IndexOf(tc) < 0) return;
        if (IsNativeTwoColumn(tc)) return;
        if (ColumnIsEntirelyVisuallyEmpty(tc, true) && !ColumnIsEntirelyVisuallyEmpty(tc, false))
            UnwrapTwoColumnPromotingFilledColumn(tc, false);
        else if (ColumnIsEntirelyVisuallyEmpty(tc, false) && !ColumnIsEntirelyVisuallyEmpty(tc, true))
            UnwrapTwoColumnPromotingFilledColumn(tc, true);
    }

    private static bool IsNativeTwoColumn(TwoColumnBlockViewModel tc)
    {
        if (!tc.Meta.TryGetValue(NativeTwoColumnMetaKey, out var raw) || raw == null)
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

    private void OnNewBlockRequested(BlockViewModel block, string? initialContent)
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
            SubscribeToBlock(newBlock);
            ReorderBlocks();
            CommitStructuralChange("Split block");
            BlocksChanged?.Invoke();
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(() => newBlock.IsFocused = true, DispatcherPriority.Render);
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

    private void OnNewBlockAboveRequested(BlockViewModel block, string? initialContent)
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
            SubscribeToBlock(newBlock);
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

        // `block` is still the same instance, now at index+1. If the guard failed before, we never focused.
        if (Blocks.IndexOf(block) < 0) return;

        if (!block.PendingCaretIndex.HasValue)
            block.PendingCaretIndex = 0;

        CommitStructuralChange("Insert block above");
        BlocksChanged?.Invoke();

        // After BlockRows rebuild, focus the shifted block. VM only notifies IsFocused when value *changes*;
        // if LostFocus hasn't cleared yet, IsFocused=true is a no-op â€” toggle falseâ†’true so FocusTextBox runs.
        Dispatcher.UIThread.Post(() =>
        {
            if (!block.PendingCaretIndex.HasValue)
                block.PendingCaretIndex = 0;
            block.IsFocused = false;
            block.IsFocused = true;
        }, DispatcherPriority.Loaded);
    }

    private void OnNewBlockOfTypeRequested(BlockViewModel block, BlockType type, string? initialContent)
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
            SubscribeToBlock(newBlock);
            ReorderBlocks();
            CommitStructuralChange("New block");
            BlocksChanged?.Invoke();
            newBlock.PendingCaretIndex = 0;
            Dispatcher.UIThread.Post(() => newBlock.IsFocused = true, DispatcherPriority.Render);
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

    private void OnDuplicateBlockRequested(BlockViewModel block)
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

    private void OnFocusPreviousRequested(BlockViewModel block, double? caretPixelX)
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

    private void OnFocusNextRequested(BlockViewModel block, double? caretPixelX)
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

    private void ReorderBlocks()
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

        // Old code did EnumerateInDocumentOrder(Blocks).Any(...) on every reorder â€” an
        // O(N) walk just to decide whether to do the O(N) update. With 1500 blocks that
        // doubled the reorder cost. The tracked set makes the guard O(1).
        if (_numberedListBlocks.Count > 0)
            UpdateListNumbers();
    }

    private void UpdateListNumbers()
    {
        if (_numberedListBlocks.Count == 0)
            return;

        int listNumber = 1;
        bool prevWasNumbered = false;
        // Avoid ToList(): stream the document-order enumeration and look back via a flag.
        foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (block.Type == BlockType.NumberedList)
            {
                if (!prevWasNumbered)
                    listNumber = Math.Max(1, block.ListNumberIndex);
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

    public void NotifyBlocksChanged()
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        if (_findPanelVisible)
        {
            var findStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
            RefreshFindMatchesAndHighlights();
            EditorPerfDiagnostics.RecordPhase(perf, findStart, "notifyBlocksChanged.findRefresh");
        }

        var titlesStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        RefreshPageBlockTitles();
        EditorPerfDiagnostics.RecordPhase(perf, titlesStart, "notifyBlocksChanged.pageTitles");

        var notifyStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        BlocksChanged?.Invoke();
        EditorPerfDiagnostics.RecordPhase(perf, notifyStart, "notifyBlocksChanged.blocksChanged");

        EditorPerfDiagnostics.RecordIfSlow(
            perf,
            "notifyBlocksChanged",
            EditorPerfDiagnostics.ElapsedMs(perfStart),
            $"top={Blocks.Count} realized={RealizedRowCount}");
    }

    public event System.Action? BlocksChanged;
    public new event PropertyChangedEventHandler? PropertyChanged;


    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


