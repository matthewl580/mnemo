using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;
using Mnemo.UI.Components.Overlays;
using Mnemo.UI.Services;

namespace Mnemo.UI.Components.BlockEditor;

public partial class BlockEditor
{
    private bool HandleFindPanelNavigationKey(KeyEventArgs e)
    {
        if (!_findPanelVisible)
            return false;
        if (e.Key == Key.Escape)
        {
            CloseFindPanel(clearQuery: false);
            e.Handled = true;
            return true;
        }

        if (!IsFocusInsideFindPanel())
            return false;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0)
            return false;

        if (e.Key == Key.Down)
        {
            NavigateFindMatches(forward: true);
            e.Handled = true;
            return true;
        }

        if (e.Key == Key.Up)
        {
            NavigateFindMatches(forward: false);
            e.Handled = true;
            return true;
        }

        return false;
    }

    private bool IsFocusInsideFindPanel()
    {
        if (_findPanel == null)
            return false;
        var top = TopLevel.GetTopLevel(this);
        if (top?.FocusManager?.GetFocusedElement() is not Visual focused)
            return false;
        return _findPanel.IsVisualAncestorOf(focused) || ReferenceEquals(focused, _findPanel);
    }

    private void OpenFindPanel()
    {
        _overlayService ??= ((App?)Application.Current)?.Services?.GetService<IOverlayService>();
        if (_overlayService == null)
            return;

        if (_findPanelVisible && _findPanel != null && !string.IsNullOrEmpty(_findOverlayId))
        {
            AttachFindOverlayScrollHost();
            UpdateFindOverlayAnchor();
            if (!string.Equals(_findPanel.FindQueryTextBox.Text ?? string.Empty, _findQuery, StringComparison.Ordinal))
                _findPanel.FindQueryTextBox.Text = _findQuery;
            UpdateFindMatchCountText();
            Dispatcher.UIThread.Post(() =>
            {
                _findPanel.FindQueryTextBox.Focus();
                _findPanel.FindQueryTextBox.SelectAll();
            }, DispatcherPriority.Input);
            RefreshFindMatchesAndHighlights();
            return;
        }

        var panel = new EditorFindPanel { EditorHost = this };
        if (!string.IsNullOrEmpty(_findQuery))
            panel.FindQueryTextBox.Text = _findQuery;
        if (!string.IsNullOrEmpty(_replaceQuery))
            panel.ReplaceQueryTextBox.Text = _replaceQuery;

        _findPanel = panel;

        TryComputeFindOverlayAnchor(this, out var anchorX, out var anchorY);
        var options = new OverlayOptions
        {
            ShowBackdrop = false,
            CloseOnOutsideClick = false,
            CloseOnEscape = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AnchorPosition = AnchorPosition.TopLeft,
            AnchorPointX = anchorX,
            AnchorPointY = anchorY
        };
        _findOverlayId = _overlayService.CreateOverlay(panel, options, "EditorFindPanel");
        _findPanelVisible = true;
        _replacePanelExpanded = false;
        _findCaretBeforeOpen = CaptureCaretState();
        _findNavigatedToMatch = false;
        _lastFindOverlayAnchorX = null;
        _lastFindOverlayAnchorY = null;
        ApplyReplacePanelUiState();
        UpdateFindMatchCountText();
        AttachFindOverlayScrollHost();
        Dispatcher.UIThread.Post(() =>
        {
            UpdateFindOverlayAnchor();
            panel.FindQueryTextBox.Focus();
            panel.FindQueryTextBox.SelectAll();
        }, DispatcherPriority.Loaded);
        RefreshFindMatchesAndHighlights();
    }

    private void CloseFindPanel(bool clearQuery)
    {
        var shouldRestoreCaret = _findCaretBeforeOpen != null && (!_findNavigatedToMatch || _findMatches.Count == 0);

        _findPanelVisible = false;
        _replacePanelExpanded = false;
        DetachFindOverlayScrollHost();
        _lastFindOverlayAnchorX = null;
        _lastFindOverlayAnchorY = null;

        if (!string.IsNullOrEmpty(_findOverlayId) && _overlayService != null)
        {
            _overlayService.CloseOverlay(_findOverlayId);
            _findOverlayId = null;
        }

        _findPanel = null;
        _findMatches.Clear();
        _activeFindMatchIndex = -1;
        if (clearQuery)
        {
            _findQuery = string.Empty;
            _replaceQuery = string.Empty;
        }
        var caretBeforeFind = _findCaretBeforeOpen;
        _findCaretBeforeOpen = null;
        _findNavigatedToMatch = false;

        ApplyFindHighlights();
        UpdateFindMatchCountText();
        if (shouldRestoreCaret && caretBeforeFind != null)
            ApplyCaretFocus(caretBeforeFind);
        else
            RestoreEditorFocusAfterFindPanelClose();
    }

    private void RestoreEditorFocusAfterFindPanelClose()
    {
        Dispatcher.UIThread.Post(() =>
        {
            BlockViewModel? target = null;
            if (!string.IsNullOrEmpty(_focusedBlockId))
                target = BlockHierarchy.FindById(Blocks, _focusedBlockId!);
            target ??= BlockHierarchy.FindFocused(Blocks);
            target ??= BlockHierarchy.EnumerateInDocumentOrder(Blocks).FirstOrDefault();
            if (target == null)
                return;

            if (target.IsFocused)
            {
                target.IsFocused = false;
                target.IsFocused = true;
            }
            else
            {
                target.IsFocused = true;
            }
        }, DispatcherPriority.Input);
    }

    private void ApplyReplacePanelUiState()
    {
        if (_findPanel == null)
            return;
        _findPanel.FindReplaceGrid.IsVisible = _replacePanelExpanded;
        _findPanel.FindToggleReplaceTextBlock.Text = _replacePanelExpanded ? "Replace on" : "Replace off";
        _findPanel.FindOptionsRow.IsVisible = !_replacePanelExpanded;

        var caseSensitive = _replacePanelExpanded
            ? (_findPanel.FindCaseSensitiveCheckBoxReplace.IsChecked ?? false)
            : (_findPanel.FindCaseSensitiveCheckBoxCompact.IsChecked ?? false);
        var wholeWord = _replacePanelExpanded
            ? (_findPanel.FindWholeWordCheckBoxReplace.IsChecked ?? false)
            : (_findPanel.FindWholeWordCheckBoxCompact.IsChecked ?? false);

        _isSyncingFindOptionToggles = true;
        try
        {
            _findPanel.FindCaseSensitiveCheckBoxReplace.IsChecked = caseSensitive;
            _findPanel.FindCaseSensitiveCheckBoxCompact.IsChecked = caseSensitive;
            _findPanel.FindWholeWordCheckBoxReplace.IsChecked = wholeWord;
            _findPanel.FindWholeWordCheckBoxCompact.IsChecked = wholeWord;
        }
        finally
        {
            _isSyncingFindOptionToggles = false;
        }
    }

    private void UpdateFindOverlayAnchor()
    {
        if (!_findPanelVisible || string.IsNullOrEmpty(_findOverlayId) || _overlayService == null || _findPanel == null)
            return;
        var instance = _overlayService.Overlays.FirstOrDefault(o => o.Id == _findOverlayId);
        if (instance == null)
            return;
        if (!TryComputeFindOverlayAnchor(this, out var x, out var y))
            return;
        if (_lastFindOverlayAnchorX is { } lx
            && _lastFindOverlayAnchorY is { } ly
            && Math.Abs(lx - x) < 0.5
            && Math.Abs(ly - y) < 0.5)
            return;

        _lastFindOverlayAnchorX = x;
        _lastFindOverlayAnchorY = y;
        instance.Options.AnchorPointX = x;
        instance.Options.AnchorPointY = y;
        instance.Options.AnchorPosition = AnchorPosition.TopLeft;
    }

    private void AttachFindOverlayScrollHost()
    {
        DetachFindOverlayScrollHost();
        _findAnchorScrollHost = this.FindAncestorOfType<ScrollViewer>();
        if (_findAnchorScrollHost == null)
            return;
        _findAnchorScrollSizeChangedHandler = (_, _) =>
        {
            _lastFindOverlayAnchorX = null;
            _lastFindOverlayAnchorY = null;
            UpdateFindOverlayAnchor();
        };
        _findAnchorScrollHost.SizeChanged += _findAnchorScrollSizeChangedHandler;
    }

    private void DetachFindOverlayScrollHost()
    {
        if (_findAnchorScrollHost != null && _findAnchorScrollSizeChangedHandler != null)
            _findAnchorScrollHost.SizeChanged -= _findAnchorScrollSizeChangedHandler;
        _findAnchorScrollHost = null;
        _findAnchorScrollSizeChangedHandler = null;
    }

    private static bool TryComputeFindOverlayAnchor(BlockEditor editor, out double anchorLeft, out double anchorTop)
    {
        const double padding = 8;
        anchorLeft = padding;
        anchorTop = padding;
        var top = TopLevel.GetTopLevel(editor);
        if (top == null)
            return false;

        var w = FindOverlayAnchorWidthEstimate;
        var scroll = editor.FindAncestorOfType<ScrollViewer>();
        if (scroll != null)
        {
            var tl = scroll.TranslatePoint(new Point(0, 0), top);
            if (!tl.HasValue)
                return false;
            anchorLeft = tl.Value.X + scroll.Bounds.Width - w - padding;
            anchorTop = tl.Value.Y + padding;
            return true;
        }

        var editorTl = editor.TranslatePoint(new Point(0, 0), top);
        if (!editorTl.HasValue)
            return false;
        anchorLeft = editorTl.Value.X + editor.Bounds.Width - w - padding;
        anchorTop = editorTl.Value.Y + padding;
        return true;
    }

    internal void OnEditorFindPanelFindQueryTextChanged(object? sender, TextChangedEventArgs e) =>
        FindQueryTextBox_OnTextChanged(sender, e);

    internal void OnEditorFindPanelReplaceQueryTextChanged(object? sender, TextChangedEventArgs e) =>
        ReplaceQueryTextBox_OnTextChanged(sender, e);

    internal void OnEditorFindPanelOptionChanged(object? sender, RoutedEventArgs e) =>
        FindOptionCheckBox_OnChanged(sender, e);

    internal void OnEditorFindPanelFindPreviousClick(object? sender, RoutedEventArgs e) =>
        FindPreviousButton_OnClick(sender, e);

    internal void OnEditorFindPanelFindNextClick(object? sender, RoutedEventArgs e) =>
        FindNextButton_OnClick(sender, e);

    internal void OnEditorFindPanelToggleReplaceClick(object? sender, RoutedEventArgs e) =>
        FindToggleReplaceButton_OnClick(sender, e);

    internal void OnEditorFindPanelCloseClick(object? sender, RoutedEventArgs e) =>
        CloseFindPanel(clearQuery: false);

    internal void OnEditorFindPanelReplaceCurrentClick(object? sender, RoutedEventArgs e) =>
        ReplaceCurrentButton_OnClick(sender, e);

    internal void OnEditorFindPanelReplaceAllClick(object? sender, RoutedEventArgs e) =>
        ReplaceAllButton_OnClick(sender, e);

    internal void OnEditorFindPanelFindTextKeyDown(object? sender, KeyEventArgs e) =>
        FindTextBox_OnKeyDown(sender, e);

    internal void OnEditorFindPanelReplaceTextKeyDown(object? sender, KeyEventArgs e) =>
        ReplaceTextBox_OnKeyDown(sender, e);

    private void FindQueryTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        _findQuery = (sender as TextBox)?.Text ?? _findPanel?.FindQueryTextBox.Text ?? string.Empty;
        RefreshFindMatchesAndHighlights();
    }

    private void ReplaceQueryTextBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        _replaceQuery = (sender as TextBox)?.Text ?? _findPanel?.ReplaceQueryTextBox.Text ?? string.Empty;
    }

    private void FindOptionCheckBox_OnChanged(object? sender, RoutedEventArgs e)
    {
        if (_findPanel != null && !_isSyncingFindOptionToggles)
        {
            _isSyncingFindOptionToggles = true;
            try
            {
                var caseSensitive = (sender == _findPanel.FindCaseSensitiveCheckBoxReplace
                                     || sender == _findPanel.FindCaseSensitiveCheckBoxCompact)
                    ? ((sender as ToggleButton)?.IsChecked ?? false)
                    : (_findPanel.FindCaseSensitiveCheckBoxReplace.IsChecked
                       ?? _findPanel.FindCaseSensitiveCheckBoxCompact.IsChecked
                       ?? false);
                var wholeWord = (sender == _findPanel.FindWholeWordCheckBoxReplace
                                 || sender == _findPanel.FindWholeWordCheckBoxCompact)
                    ? ((sender as ToggleButton)?.IsChecked ?? false)
                    : (_findPanel.FindWholeWordCheckBoxReplace.IsChecked
                       ?? _findPanel.FindWholeWordCheckBoxCompact.IsChecked
                       ?? false);

                _findPanel.FindCaseSensitiveCheckBoxReplace.IsChecked = caseSensitive;
                _findPanel.FindCaseSensitiveCheckBoxCompact.IsChecked = caseSensitive;
                _findPanel.FindWholeWordCheckBoxReplace.IsChecked = wholeWord;
                _findPanel.FindWholeWordCheckBoxCompact.IsChecked = wholeWord;
            }
            finally
            {
                _isSyncingFindOptionToggles = false;
            }
        }

        RefreshFindMatchesAndHighlights();
    }

    private void FindPreviousButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFindMatches(forward: false);

    private void FindNextButton_OnClick(object? sender, RoutedEventArgs e) => NavigateFindMatches(forward: true);

    private void FindToggleReplaceButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _replacePanelExpanded = !_replacePanelExpanded;
        ApplyReplacePanelUiState();
        if (_replacePanelExpanded && _findPanel != null)
            Dispatcher.UIThread.Post(() => _findPanel.ReplaceQueryTextBox.Focus(), DispatcherPriority.Input);
    }

    private void ReplaceCurrentButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReplaceCurrentFindMatch();
    }

    private void ReplaceAllButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ReplaceAllFindMatches();
    }

    private void FindTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            NavigateFindMatches(forward: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            NavigateFindMatches(forward: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            NavigateFindMatches(forward: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up)
        {
            NavigateFindMatches(forward: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            CloseFindPanel(clearQuery: false);
            e.Handled = true;
        }
    }

    private void ReplaceTextBox_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ReplaceCurrentFindMatch();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Down)
        {
            NavigateFindMatches(forward: true);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up)
        {
            NavigateFindMatches(forward: false);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            CloseFindPanel(clearQuery: false);
            e.Handled = true;
        }
    }

    private void RefreshFindMatchesAndHighlights()
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        var rebuildStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        RebuildFindMatches();
        EditorPerfDiagnostics.RecordPhase(perf, rebuildStart, "find.rebuildMatches", $"matches={_findMatches.Count}");

        var applyStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;
        ApplyFindHighlights();
        EditorPerfDiagnostics.RecordPhase(perf, applyStart, "find.applyHighlights");

        UpdateFindMatchCountText();

        EditorPerfDiagnostics.RecordIfSlow(
            perf,
            "find.refresh",
            EditorPerfDiagnostics.ElapsedMs(perfStart),
            $"top={Blocks.Count} matches={_findMatches.Count} realized={RealizedRowCount}");
    }

    private void RebuildFindMatches()
    {
        var previousActive = _activeFindMatchIndex >= 0 && _activeFindMatchIndex < _findMatches.Count
            ? _findMatches[_activeFindMatchIndex]
            : (FindMatch?)null;
        _findMatches.Clear();
        _activeFindMatchIndex = -1;

        if (string.IsNullOrEmpty(_findQuery))
            return;

        foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            if (!IsFindSearchableBlock(block))
                continue;
            var editable = GetEditableBlockForViewModel(block);
            var text = editable?.TryGetRichTextEditor()?.Text ?? block.Content ?? string.Empty;
            if (string.IsNullOrEmpty(text))
                continue;

            foreach (var start in FindMatchOffsets(text, _findQuery, IsFindCaseSensitive(), IsFindWholeWord()))
                _findMatches.Add(new FindMatch(block.Id, start, _findQuery.Length));
        }

        if (_findMatches.Count == 0)
            return;

        if (previousActive is { } oldActive)
        {
            var preservedIndex = _findMatches.FindIndex(m =>
                m.BlockId == oldActive.BlockId
                && m.Start == oldActive.Start
                && m.Length == oldActive.Length);
            if (preservedIndex >= 0)
            {
                _activeFindMatchIndex = preservedIndex;
                return;
            }
        }

        _activeFindMatchIndex = 0;
    }

    private void ApplyFindHighlights()
    {
        var byBlockId = _findMatches.GroupBy(m => m.BlockId).ToDictionary(g => g.Key, g => g.ToList());
        FindMatch? active = _activeFindMatchIndex >= 0 && _activeFindMatchIndex < _findMatches.Count
            ? _findMatches[_activeFindMatchIndex]
            : null;

        foreach (var block in BlockHierarchy.EnumerateInDocumentOrder(Blocks))
        {
            var rte = GetEditableBlockForViewModel(block)?.TryGetRichTextEditor();
            if (rte == null)
                continue;

            if (!byBlockId.TryGetValue(block.Id, out var rangesForBlock))
            {
                rte.SearchHighlightRanges = Array.Empty<RichTextEditor.SearchHighlightRange>();
                rte.ActiveSearchHighlightRange = null;
                continue;
            }

            rte.SearchHighlightRanges = rangesForBlock
                .Select(m => new RichTextEditor.SearchHighlightRange(m.Start, m.Length))
                .ToArray();

            if (active is { } current && current.BlockId == block.Id)
                rte.ActiveSearchHighlightRange = new RichTextEditor.SearchHighlightRange(current.Start, current.Length);
            else
                rte.ActiveSearchHighlightRange = null;
        }
    }

    private void UpdateFindMatchCountText()
    {
        if (_findPanel?.FindMatchCountTextBlock is not { } countBlock)
            return;
        if (string.IsNullOrEmpty(_findQuery))
        {
            countBlock.Text = "0/0";
            return;
        }
        if (_findMatches.Count == 0)
        {
            countBlock.Text = "0";
            return;
        }
        countBlock.Text = $"{_activeFindMatchIndex + 1}/{_findMatches.Count}";
    }

    private void NavigateFindMatches(bool forward)
    {
        if (_findMatches.Count == 0)
        {
            UpdateFindMatchCountText();
            return;
        }

        _activeFindMatchIndex = forward
            ? (_activeFindMatchIndex + 1 + _findMatches.Count) % _findMatches.Count
            : (_activeFindMatchIndex - 1 + _findMatches.Count) % _findMatches.Count;

        _findNavigatedToMatch = true;
        FocusFindMatch(_findMatches[_activeFindMatchIndex]);
        ApplyFindHighlights();
        UpdateFindMatchCountText();
    }

    private void FocusFindMatch(FindMatch match)
    {
        var block = BlockHierarchy.FindById(Blocks, match.BlockId);
        if (block == null)
            return;
        var editable = GetEditableBlockForViewModel(block);
        if (editable == null)
            return;

        ClearTextSelectionInAllBlocksExcept(block);
        block.IsFocused = true;
        editable.ApplyTextSelection(match.Start, match.Start + match.Length);
        editable.BringIntoView();
    }

    private void ReplaceCurrentFindMatch()
    {
        if (_activeFindMatchIndex < 0 || _activeFindMatchIndex >= _findMatches.Count)
            return;
        var current = _findMatches[_activeFindMatchIndex];
        var block = BlockHierarchy.FindById(Blocks, current.BlockId);
        if (block == null)
            return;
        var editable = GetEditableBlockForViewModel(block);
        if (editable == null)
            return;

        BeginStructuralChange();
        editable.ApplyTextSelection(current.Start, current.Start + current.Length);
        if (editable.InsertTextAtCursor(_replaceQuery))
        {
            CommitStructuralChange("Replace text");
            NotifyBlocksChanged();
        }
        else
        {
            CommitStructuralChange("Replace text");
        }
        RefreshFindMatchesAndHighlights();
    }

    private void ReplaceAllFindMatches()
    {
        if (_findMatches.Count == 0)
            return;

        var matchesByBlock = _findMatches.GroupBy(m => m.BlockId).ToList();
        var didChange = false;
        BeginStructuralChange();
        foreach (var group in matchesByBlock)
        {
            var block = BlockHierarchy.FindById(Blocks, group.Key);
            if (block == null)
                continue;
            var editable = GetEditableBlockForViewModel(block);
            var rte = editable?.TryGetRichTextEditor();
            var originalFlat = rte?.Text ?? block.Content ?? string.Empty;
            if (string.IsNullOrEmpty(originalFlat))
                continue;
            var newFlat = originalFlat;

            foreach (var match in group.OrderByDescending(m => m.Start))
            {
                if (match.Start < 0 || match.Start + match.Length > newFlat.Length)
                    continue;
                newFlat = newFlat.Remove(match.Start, match.Length).Insert(match.Start, _replaceQuery);
            }

            if (string.Equals(originalFlat, newFlat, StringComparison.Ordinal))
                continue;

            var sourceRuns = rte?.Spans ?? block.Spans;
            var updatedRuns = InlineSpanFormatApplier.ApplyTextEdit(sourceRuns, originalFlat, newFlat);
            if (rte != null)
                rte.Spans = updatedRuns;
            block.CommitSpansFromEditor(updatedRuns);
            didChange = true;
        }

        if (didChange)
        {
            CommitStructuralChange("Replace all text");
            NotifyBlocksChanged();
        }
        else
        {
            CommitStructuralChange("Replace all text");
        }

        RefreshFindMatchesAndHighlights();
    }

    private bool IsFindSearchableBlock(BlockViewModel block) =>
        block.Type is not BlockType.Divider and not BlockType.Image and not BlockType.Equation and not BlockType.Code
        and not BlockType.Page;

    private bool IsFindCaseSensitive() =>
        _findPanel != null
        && ((_findPanel.FindCaseSensitiveCheckBoxReplace.IsChecked ?? false)
            || (_findPanel.FindCaseSensitiveCheckBoxCompact.IsChecked ?? false));

    private bool IsFindWholeWord() =>
        _findPanel != null
        && ((_findPanel.FindWholeWordCheckBoxReplace.IsChecked ?? false)
            || (_findPanel.FindWholeWordCheckBoxCompact.IsChecked ?? false));

    private static IEnumerable<int> FindMatchOffsets(string text, string query, bool caseSensitive, bool wholeWord)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
            yield break;

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = 0;
        while (start <= text.Length - query.Length)
        {
            var idx = text.IndexOf(query, start, comparison);
            if (idx < 0)
                yield break;
            if (!wholeWord || IsWholeWordBoundary(text, idx, query.Length))
                yield return idx;
            start = idx + Math.Max(query.Length, 1);
        }
    }

    private static bool IsWholeWordBoundary(string text, int start, int length)
    {
        var beforeIsWord = start > 0 && IsWordChar(text[start - 1]);
        var afterIndex = start + length;
        var afterIsWord = afterIndex < text.Length && IsWordChar(text[afterIndex]);
        return !beforeIsWord && !afterIsWord;
    }

    private static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '_';

    private void TryPopulateFindQueryFromFocusedSelection()
    {
        if (!_findPanelVisible || _findPanel == null)
            return;

        BlockViewModel? focused = null;
        if (!string.IsNullOrEmpty(_focusedBlockId))
            focused = BlockHierarchy.FindById(Blocks, _focusedBlockId!);
        focused ??= BlockHierarchy.FindFocused(Blocks);
        if (focused == null)
            return;

        var editable = GetEditableBlockForViewModel(focused);
        var selected = editable?.GetSelectedText();
        if (string.IsNullOrEmpty(selected))
            return;

        if (!string.Equals(_findPanel.FindQueryTextBox.Text, selected, StringComparison.Ordinal))
            _findPanel.FindQueryTextBox.Text = selected;
        _findQuery = selected;
    }
}