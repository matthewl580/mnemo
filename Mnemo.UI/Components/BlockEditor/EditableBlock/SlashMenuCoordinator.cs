using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mnemo.Core.Models;
using Mnemo.UI.Components.Overlays;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Manages the slash-command menu overlay lifecycle for a single <see cref="EditableBlock"/>.
/// Owns <c>_slashMenuOverlayId</c> and <c>_currentSlashMenu</c>.
/// </summary>
internal sealed class SlashMenuCoordinator
{
    private readonly EditableBlock _host;
    private string? _slashMenuOverlayId;
    private SlashCommandMenu? _currentSlashMenu;

    private const double HeightEstimate = 320;

    internal SlashMenuCoordinator(EditableBlock host)
    {
        _host = host;
    }

    internal bool IsVisible => _slashMenuOverlayId != null;

    internal void HandleTextChanged(string text, RichTextEditor textBox)
    {
        if (_host._stateManager == null) return;

        var filterSource = BlockEditorContentPolicy.WithoutLegacySentinel(text);
        var isSlashCommand = filterSource.StartsWith("/");

        if (isSlashCommand && !IsVisible)
        {
            Show(textBox, filterSource);
            _host._stateManager.SetShowingSlashMenu();
        }
        else if (isSlashCommand && IsVisible && _currentSlashMenu != null)
        {
            _currentSlashMenu.UpdateFilter(filterSource);
        }
        else if (!isSlashCommand && IsVisible)
        {
            Close();
            _host._stateManager.SetNormal();
        }
    }

    internal bool TryNavigate(Key key)
    {
        if (_currentSlashMenu == null || !IsVisible) return false;
        if (key == Key.Up) { _currentSlashMenu.HandleUp(); return true; }
        if (key == Key.Down) { _currentSlashMenu.HandleDown(); return true; }
        return false;
    }

    internal bool TryConfirm()
    {
        if (_currentSlashMenu == null || !IsVisible) return false;
        _currentSlashMenu.HandleEnter();
        return true;
    }

    internal void OnEscape()
    {
        if (!IsVisible || _host._stateManager == null) return;
        Close();
        _host._stateManager.SetNormal();
    }

    internal void Close()
    {
        if (string.IsNullOrEmpty(_slashMenuOverlayId) || _host._overlayService == null) return;
        _host._overlayService.CloseOverlay(_slashMenuOverlayId);
        _slashMenuOverlayId = null;
        _currentSlashMenu = null;
    }

    private void Show(RichTextEditor textBox, string filterText = "")
    {
        if (_host._overlayService == null || !textBox.IsVisible) return;

        Close();

        var menu = new SlashCommandMenu();
        menu.CommandSelected += OnCommandSelected;
        menu.UpdateFilter(filterText);
        _currentSlashMenu = menu;

        bool showAbove = ShouldShowAbove(textBox);
        var options = new OverlayOptions
        {
            ShowBackdrop = false,
            CloseOnOutsideClick = true,
            AnchorControl = textBox,
            AnchorPosition = showAbove ? AnchorPosition.TopLeft : AnchorPosition.BottomLeft,
            AnchorOffset = showAbove ? new Thickness(0, -4, 0, 0) : new Thickness(0, 4, 0, 0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };

        _slashMenuOverlayId = _host._overlayService.CreateOverlay(menu, options, "SlashCommandMenu");
    }

    private void OnCommandSelected(BlockType blockType)
    {
        if (_host._viewModel == null || _host._stateManager == null) return;

        Close();

        if (blockType == BlockType.TwoColumn)
        {
            if (_host._viewModel.OwnerTwoColumn != null)
            {
                _host._stateManager.SetNormal();
                _host._focusManager?.ClearCache();
                return;
            }

            var editor = _host.FindParentBlockEditor();
            if (editor != null)
            {
                _host._stateManager.PreviousText = string.Empty;
                editor.ReplaceBlockWithTwoColumn(_host._viewModel);
                _host._stateManager.SetNormal();
                _host._focusManager?.ClearCache();
                return;
            }
        }

        if (blockType == BlockType.Page)
        {
            var editor = _host.FindParentBlockEditor();
            if (editor == null
                || string.IsNullOrEmpty(editor.HostNoteId)
                || editor.CreateChildPageUnderNoteAsync == null)
            {
                _host._stateManager.SetNormal();
                _host._focusManager?.ClearCache();
                return;
            }

            _ = CompleteSlashPageBlockAsync(editor);
            return;
        }

        _host._viewModel.NotifyStructuralChangeStarting();
        _host._viewModel.Type = blockType;
        _host._viewModel.Content = blockType == BlockType.Sketch ? "A -> B" : string.Empty;
        _host._stateManager.PreviousText = _host._viewModel.Content ?? string.Empty;
        _host._stateManager.SetNormal();
        _host._focusManager?.ClearCache();

        var addedBlockBelow = blockType is BlockType.Divider or BlockType.Image or BlockType.Equation or BlockType.Sketch
            && _host.EnsureEditableBlockBelowIfNeeded();

        if (!addedBlockBelow)
        {
            _host._viewModel.IsFocused = true;
            Dispatcher.UIThread.Post(() => _host._focusManager?.FocusTextBox(), DispatcherPriority.Loaded);
        }
    }

    private async System.Threading.Tasks.Task CompleteSlashPageBlockAsync(BlockEditor editor)
    {
        if (_host._viewModel == null || _host._stateManager == null) return;
        try
        {
            var newId = await editor.CreateChildPageUnderNoteAsync!(editor.HostNoteId!).ConfigureAwait(true);
            if (string.IsNullOrEmpty(newId)) return;

            _host._viewModel.NotifyStructuralChangeStarting();
            // Id must be set before Type: the Type change commits the structural snapshot
            // ("Change block type"), and a Page block snapshotted without its ReferenceNoteId
            // restores as "Missing note" on undo/redo.
            _host._viewModel.ReferenceNoteId = newId;
            _host._viewModel.Type = BlockType.Page;
            _host._viewModel.RefreshPageButtonTitle(editor.NoteTitleResolver, editor.PageBlockMissingTitle, editor.ChildPageCountResolver);

            var added = _host.EnsureEditableBlockBelowIfNeeded();
            if (!added)
            {
                _host._viewModel.IsFocused = true;
                Dispatcher.UIThread.Post(() => _host._focusManager?.FocusTextBox(), DispatcherPriority.Loaded);
            }

            if (editor.FlushPendingNoteSaveAsync != null)
                await editor.FlushPendingNoteSaveAsync().ConfigureAwait(true);

            editor.NotifyBlocksChanged();
        }
        finally
        {
            _host._stateManager.SetNormal();
            _host._focusManager?.ClearCache();
        }
    }

    private static bool ShouldShowAbove(Control textBox)
    {
        if (!textBox.IsVisible) return false;

        var scrollViewer = textBox.FindAncestorOfType<ScrollViewer>();
        double visibleTop, visibleBottom, anchorTop, anchorBottom;

        if (scrollViewer != null && scrollViewer.Content is Visual scrollContent)
        {
            var pt = textBox.TranslatePoint(new Point(0, 0), scrollContent);
            if (!pt.HasValue) return false;
            visibleTop = scrollViewer.Offset.Y;
            visibleBottom = scrollViewer.Offset.Y + scrollViewer.Viewport.Height;
            anchorTop = pt.Value.Y;
            anchorBottom = pt.Value.Y + textBox.Bounds.Height;
        }
        else
        {
            var topLevel = textBox.FindAncestorOfType<TopLevel>();
            if (topLevel == null) return false;
            var pt = textBox.TranslatePoint(new Point(0, 0), topLevel);
            if (!pt.HasValue) return false;
            visibleTop = 0;
            visibleBottom = topLevel.Bounds.Height;
            anchorTop = pt.Value.Y;
            anchorBottom = pt.Value.Y + textBox.Bounds.Height;
        }

        double spaceAbove = anchorTop - visibleTop;
        double spaceBelow = visibleBottom - anchorBottom;

        if (spaceBelow < HeightEstimate && spaceAbove >= spaceBelow) return true;
        if (spaceAbove < HeightEstimate && spaceBelow >= spaceAbove) return false;
        return false;
    }
}
