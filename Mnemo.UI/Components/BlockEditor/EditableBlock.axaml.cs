using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.UI.Components.BlockEditor.BlockComponents;
using Mnemo.UI.Components.Overlays;
using System;
using Mnemo.UI;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Editable block control: DI resolution, component wiring, and event routing to coordinators.
/// </summary>
public partial class EditableBlock : UserControl
{
    // ── Existing manager instances ───────────────────────────────────────────
    private KeyboardHandler? _keyboardHandler;
    internal FocusManager? _focusManager;
    private MarkdownShortcutDetector? _markdownDetector;
    internal EditorStateManager? _stateManager;

    // ── Coordinator instances ────────────────────────────────────────────────
    internal FormattingToolbarCoordinator? _toolbar;
    internal SlashMenuCoordinator? _slashCoord;
    internal InlineFormatCommandHandler? _formatHandler;
    internal BlockChromeController? _chrome;

    // ── Services ─────────────────────────────────────────────────────────────
    internal IOverlayService? _overlayService;

    // ── Mutable state ────────────────────────────────────────────────────────
    internal BlockViewModel? _viewModel;
    private BlockEditor? _cachedParentEditor;
    internal BlockComponentBase? _currentBlockComponent;
    private RichTextEditor? _currentEditor;
    private IDisposable? _selectionStartSubscription;
    private IDisposable? _selectionEndSubscription;
    private EventHandler<KeyEventArgs>? _backspaceTunnelHandler;
    private bool _backspaceHandledInTunnel;
    private bool _keymapTextCaptureActive;

    internal bool IsReadOnly => FindParentBlockEditor()?.IsReadOnly == true;

    /// <summary>Column cells hide the add/drag gutter; the parent row keeps the handle.</summary>
    public bool IsNestedInColumn
    {
        get => _chrome?.IsNestedInColumn ?? false;
        set => _chrome?.SetNestedInColumn(value);
    }

    public EditableBlock()
    {
        InitializeComponent();
        InitializeManagers();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnControlLoaded;
        Unloaded += OnControlUnloaded;
        LayoutUpdated += (_, _) => _chrome?.UpdateGutterVerticalAlignment();

        SetupDragDrop();
    }

    private void InitializeManagers()
    {
        _stateManager = new EditorStateManager();
        _keyboardHandler = new KeyboardHandler();
        _markdownDetector = new MarkdownShortcutDetector();
        _focusManager = new FocusManager(this);
        _overlayService = ((App)Application.Current!).Services!.GetService<IOverlayService>();

        _toolbar = new FormattingToolbarCoordinator(this);
        _slashCoord = new SlashMenuCoordinator(this);
        _formatHandler = new InlineFormatCommandHandler(this);
        _chrome = new BlockChromeController(this);

        _keyboardHandler.BackspaceOnEmpty += HandleBackspaceOnEmptyBlock;
        _keyboardHandler.RequestFocusPrevious += HandleRequestFocusPrevious;
        _keyboardHandler.RequestFocusNext += HandleRequestFocusNext;
        _keyboardHandler.EnterPressed += HandleEnterPressed;
        _keyboardHandler.ConvertToBlockType += ConvertToBlockType;
        _keyboardHandler.ConvertToTextPreservingContent += HandleConvertToTextPreservingContent;
        _keyboardHandler.EscapePressed += OnEscapePressed;
        _keyboardHandler.MergeWithPrevious += HandleMergeWithPrevious;

        _markdownDetector.ShortcutDetected += OnMarkdownShortcutDetected;
    }

    // ── Keyboard/manager event adapters ─────────────────────────────────────

    private void HandleRequestFocusPrevious(double? caretPixelX) => _viewModel?.RequestFocusPrevious(caretPixelX);
    private void HandleRequestFocusNext(double? caretPixelX) => _viewModel?.RequestFocusNext(caretPixelX);
    private void HandleMergeWithPrevious() => _viewModel?.RequestMergeWithPrevious();

    private void HandleEnterPressed()
    {
        if (_viewModel == null) return;
        var editor = _currentBlockComponent?.GetRichTextEditor();
        if (editor != null)
        {
            var text = editor.Text;
            var selA = editor.SelectionStart;
            var selB = editor.SelectionEnd;
            var selStart = Math.Min(selA, selB);
            var selEnd = Math.Max(selA, selB);
            var selectionLength = selEnd - selStart;
            if (selectionLength > 0) text = text.Remove(selStart, selectionLength);
            var caretIndex = Math.Clamp(selectionLength > 0 ? selStart : editor.CaretIndex, 0, text.Length);

            // Enter on an empty line escapes the split only from the LAST cell of a column
            // (creates a top-level block below the row). For cells higher up the stack the
            // normal path below inserts a sibling inside the column instead of jumping out.
            if (_viewModel.OwnerTwoColumn is TwoColumnBlockViewModel ownerSplit
                && IsLastCellInItsColumn(ownerSplit, _viewModel)
                && QuoteEnterBehavior.TryGetSplitOnEmptyLineEnter(text, caretIndex, out var splitBody, out var splitFollowing))
            {
                _viewModel.NotifyStructuralChangeStarting();
                _viewModel.Content = splitBody;
                _viewModel.RequestExitSplitBelow(splitFollowing);
                return;
            }

            if (_viewModel.Type == BlockType.Quote
                && QuoteEnterBehavior.TryGetSplitOnEmptyLineEnter(text, caretIndex, out var quoteBody, out var followingText))
            {
                _viewModel.NotifyStructuralChangeStarting();
                _viewModel.Content = quoteBody;
                _viewModel.RequestNewBlock(followingText);
                return;
            }

            var isNonEmpty = !BlockEditorContentPolicy.IsVisuallyEmpty(text);
            var isLogicalStart = caretIndex == 0
                || (caretIndex == 1 && text.Length > 0 && text[0] == BlockEditorContentPolicy.LegacyParagraphSentinel);
            if (selectionLength == 0 && isLogicalStart && isNonEmpty)
            {
                _viewModel.NotifyStructuralChangeStarting();
                _viewModel.PendingCaretIndex = caretIndex;
                _viewModel.RequestNewBlockAbove();
                return;
            }

            var textBefore = text.Substring(0, caretIndex);
            var textAfter = text.Substring(caretIndex);
            _viewModel.NotifyStructuralChangeStarting();
            _viewModel.Content = textBefore;

            if (_viewModel.Type is BlockType.BulletList or BlockType.NumberedList or BlockType.Checklist)
                _viewModel.RequestNewBlockOfType(_viewModel.Type, textAfter);
            else
                _viewModel.RequestNewBlock(textAfter);
        }
        else
        {
            _viewModel.NotifyStructuralChangeStarting();
            _viewModel.RequestNewBlock();
        }
    }

    private static bool IsLastCellInItsColumn(TwoColumnBlockViewModel split, BlockViewModel cell)
    {
        var col = cell.IsLeftColumn ? split.LeftColumnBlocks : split.RightColumnBlocks;
        return col.Count > 0 && ReferenceEquals(col[col.Count - 1], cell);
    }

    private void OnMarkdownShortcutDetected(BlockType type, System.Collections.Generic.Dictionary<string, object>? meta)
        => SetBlockType(type, meta);

    private void OnEscapePressed() => _slashCoord?.OnEscape();

    private void HandleBackspaceOnEmptyBlock()
    {
        if (_keyboardHandler == null || _viewModel == null) return;
        _keyboardHandler.HandleBackspaceOnEmptyBlock(_viewModel);
    }

    // ── Public API for keybind dispatch ─────────────────────────────────────

    /// <summary>Applies an inline format to this block's current selection (cross-block paste path).</summary>
    internal void ApplyInlineFormatInternal(InlineFormatKind kind, string? color = null)
        => _formatHandler?.ApplyInternal(kind, color);

    /// <summary>Invoked from <c>EditorKeybindDispatch</c> when a manifest chord matches (tunnel phase).</summary>
    internal void TryApplyEditorKeybind(InlineFormatKind kind, RichTextEditor editor)
        => _formatHandler?.TryApplyKeybind(kind, editor);

    /// <summary>Ctrl+Shift+L: link shortcut delegated from keybind dispatch.</summary>
    internal System.Threading.Tasks.Task HandleLinkShortcutAsync(RichTextEditor editor)
        => _toolbar?.HandleLinkShortcutAsync(editor) ?? System.Threading.Tasks.Task.CompletedTask;

    /// <summary>Called by BlockEditor when selection was set by cross-block drag.</summary>
    public void NotifySelectionChangedByEditor()
        => Dispatcher.UIThread.Post(() => _toolbar?.CheckAndToggle(), DispatcherPriority.Input);

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void OnControlUnloaded(object? sender, RoutedEventArgs e) => CleanupEventHandlers();

    private void CleanupEventHandlers()
    {
        if (_viewModel != null)
            _cachedParentEditor?.UnregisterRealizedEditableBlock(_viewModel, this);

        UnsubscribeFromViewModel();
        UnsubscribeFromManagers();

        if (_currentBlockComponent != null)
        {
            _currentBlockComponent.TextBoxGotFocus -= HandleBlockComponentGotFocus;
            _currentBlockComponent.LegacyTextBoxGotFocus -= HandleLegacyTextBoxGotFocus;
            _currentBlockComponent.TextBoxLostFocus -= HandleBlockComponentLostFocus;
            _currentBlockComponent.TextBoxTextChanged -= HandleBlockComponentTextChanged;
            _currentBlockComponent.TextBoxKeyDown -= HandleBlockComponentKeyDown;
            RemoveBackspaceTunnelHandler();
            _currentBlockComponent = null;
        }

        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnControlLoaded;
        Unloaded -= OnControlUnloaded;

        _cachedParentEditor = null;
        _focusManager?.ClearCache();
    }

    private void UnsubscribeFromManagers()
    {
        if (_keyboardHandler != null)
        {
            _keyboardHandler.BackspaceOnEmpty -= HandleBackspaceOnEmptyBlock;
            _keyboardHandler.RequestFocusPrevious -= HandleRequestFocusPrevious;
            _keyboardHandler.RequestFocusNext -= HandleRequestFocusNext;
            _keyboardHandler.EnterPressed -= HandleEnterPressed;
            _keyboardHandler.ConvertToBlockType -= ConvertToBlockType;
            _keyboardHandler.ConvertToTextPreservingContent -= HandleConvertToTextPreservingContent;
            _keyboardHandler.EscapePressed -= OnEscapePressed;
            _keyboardHandler.MergeWithPrevious -= HandleMergeWithPrevious;
        }

        if (_markdownDetector != null)
            _markdownDetector.ShortcutDetected -= OnMarkdownShortcutDetected;

        _slashCoord?.Close();
        _toolbar?.Close(disposeToolbar: true);
    }

    private void SetupDragDrop()
    {
        if (BlockContainer == null) return;
        DragDrop.SetAllowDrop(BlockContainer, true);
        BlockContainer.AddHandler(DragDrop.DragOverEvent, Block_DragOver);
        BlockContainer.AddHandler(DragDrop.DropEvent, Block_Drop);
        BlockContainer.AddHandler(DragDrop.DragLeaveEvent, Block_DragLeave);
    }
}
