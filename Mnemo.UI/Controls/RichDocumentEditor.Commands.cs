using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.History;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Services.Notes.Markdown;
using Mnemo.UI.Components.BlockEditor;
using Mnemo.UI.Components.Overlays;


namespace Mnemo.UI.Controls;
public partial class RichDocumentEditor
{

    // â”€â”€ Public API â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public bool TryWrapSelectionWithCloze(int ordinal)
    {
        if (IsReadOnly || IsPreviewMode || ordinal <= 0)
            return false;
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        if (start < 0 || end < start)
            return false;

        var selectedText = end > start && Editor.Text.Length >= end
            ? Editor.Text[start..end]
            : string.Empty;
        var wrapped = selectedText.Length > 0
            ? $"{{{{c{ordinal}::{selectedText}}}}}"
            : $"{{{{c{ordinal}::}}}}";

        Editor.InsertTextAtCaret(wrapped);
        if (selectedText.Length == 0)
        {
            var insideCaret = Math.Max(0, Editor.CaretIndex - 2);
            Editor.CaretIndex = insideCaret;
            Editor.SelectionStart = insideCaret;
            Editor.SelectionEnd = insideCaret;
        }

        return true;
    }

    public void LoadSpans(IReadOnlyList<InlineSpan> spans)
    {
        _isSyncingFromProperty = true;
        try
        {
            var normalized = InlineSpanFormatApplier.Normalize(
                spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) });

            ExtractImageEntries(normalized);
            var textOnly = BuildTextOnlySpans(normalized);

            Spans = normalized;
            Editor.Spans = textOnly;

            UpdatePreviewMarkdown();
            SyncImageViewItems();
            _historyBaseline = CaptureSnapshot();
            UpdateToolbarFormatState();
        }
        finally
        {
            _isSyncingFromProperty = false;
        }
    }

    // â”€â”€ Editor events â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isSyncingFromProperty || _isHandlingEditorTextChanged)
            return;

        var editorSpans = InlineSpanFormatApplier.Normalize(
            Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) });

        // Absorb any image tokens the user pasted into the text editor.
        var editorFlat = InlineSpanFormatApplier.Flatten(editorSpans);
        var imageMatches = EmbeddedImageRegex.Matches(editorFlat);
        if (imageMatches.Count > 0)
        {
            foreach (Match m in imageMatches)
            {
                var path = m.Groups["path"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                    _imageEntries.Add((path, NormalizeAlign(m.Groups["align"].Value)));
            }

            var stripped = StripImageTokensFromText(editorFlat);
            _isSyncingFromProperty = true;
            try
            {
                editorSpans = string.Equals(stripped, editorFlat, StringComparison.Ordinal)
                    ? editorSpans
                    : InlineSpanFormatApplier.Normalize(
                        InlineSpanFormatApplier.ApplyTextEdit(editorSpans, editorFlat, stripped));
                Editor.Spans = editorSpans;
            }
            finally { _isSyncingFromProperty = false; }
        }

        var combined = BuildCombinedSpans(editorSpans);
        if (SpansEquivalent(combined, Spans))
            return;

        _isHandlingEditorTextChanged = true;
        try
        {
            _isSyncingFromProperty = true;
            Spans = combined;
        }
        finally
        {
            _isSyncingFromProperty = false;
            _isHandlingEditorTextChanged = false;
        }

        UpdatePreviewMarkdown();
        SyncImageViewItems();
        UpdateToolbarFormatState();
        SpansChanged?.Invoke(Spans);
        CommitHistoryIfChanged("Edit text");
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsPreviewMode)
            return;

        if (e.Key == Key.Escape && _stickySubSup != null)
        {
            ClearStickySubSup();
            e.Handled = true;
            return;
        }

        if (!IsReadOnly
            && e.Key == Key.Enter
            && (e.KeyModifiers & KeyModifiers.Control) == 0
            && (e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            Editor.InsertTextAtCaret("\n");
            e.Handled = true;
            return;
        }

        if ((e.KeyModifiers & KeyModifiers.Control) == 0 || (e.KeyModifiers & KeyModifiers.Alt) != 0)
            return;

        if (e.Key == Key.C)
        {
            _ = CopySelectionAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.X)
        {
            if (IsReadOnly)
                return;
            _ = CutSelectionAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.V)
        {
            if (IsReadOnly)
                return;
            _ = PasteSelectionAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Y || (e.Key == Key.Z && e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            _ = RedoAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Z)
        {
            _ = UndoAsync();
            e.Handled = true;
            return;
        }

        if (IsReadOnly)
            return;

        // Clear sticky sub/sup on navigation (formatting chords are handled via KeyMap tunnel).
        if (IsNavigationKey(e.Key))
        {
            bool isRight = e.Key == Key.Right;
            if (_stickySubSup != null)
                ClearStickySubSup();
            if (isRight)
                Editor?.SetPendingSubSup(false, false);
        }
    }

    internal void TryApplyKeybindFormat(InlineFormatKind kind)
    {
        if (!IsToolbarEnabled)
            return;
        switch (kind)
        {
            case InlineFormatKind.Subscript:
            case InlineFormatKind.Superscript:
                HandleSubSupShortcut(kind);
                return;
            case InlineFormatKind.Link:
                _ = EditLinkAsync();
                return;
            case InlineFormatKind.Highlight:
                ApplyInlineFormat(InlineFormatKind.Highlight, ResolveHighlightColor());
                return;
            default:
                ApplyInlineFormat(kind);
                return;
        }
    }

    private void OnEditorKeyUp(object? sender, KeyEventArgs e)
    {
        Dispatcher.UIThread.Post(UpdateToolbarFormatState, DispatcherPriority.Input);
    }

    private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_stickySubSup != null)
            ClearStickySubSup();
        Dispatcher.UIThread.Post(UpdateToolbarFormatState, DispatcherPriority.Input);
    }

    // â”€â”€ Toolbar button handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnBoldClick(object? sender, RoutedEventArgs e) => ApplyInlineFormat(InlineFormatKind.Bold);

    private void OnItalicClick(object? sender, RoutedEventArgs e) => ApplyInlineFormat(InlineFormatKind.Italic);

    private void OnUnderlineClick(object? sender, RoutedEventArgs e) => ApplyInlineFormat(InlineFormatKind.Underline);

    private void OnStrikethroughClick(object? sender, RoutedEventArgs e) => ApplyInlineFormat(InlineFormatKind.Strikethrough);

    private void OnHighlightClick(object? sender, RoutedEventArgs e) => ApplyInlineFormat(InlineFormatKind.Highlight, ResolveHighlightColor());

    private void OnSubscriptClick(object? sender, RoutedEventArgs e) => HandleSubSupShortcut(InlineFormatKind.Subscript);

    private void OnSuperscriptClick(object? sender, RoutedEventArgs e) => HandleSubSupShortcut(InlineFormatKind.Superscript);

    private void OnWriteModeClick(object? sender, RoutedEventArgs e) => IsPreviewMode = false;

    private void OnPreviewModeClick(object? sender, RoutedEventArgs e) => IsPreviewMode = true;

    private void HandleSubSupShortcut(InlineFormatKind kind)
    {
        var range = GetSelectionRange();
        if (range != null)
        {
            ClearStickySubSup();
            ApplyInlineFormatToRange(range.Value.Start, range.Value.End, kind, null, preserveSelection: true);
            return;
        }

        var newKind = _stickySubSup == kind ? (InlineFormatKind?)null : kind;
        _stickySubSup = newKind;
        if (newKind == InlineFormatKind.Subscript)
            Editor?.SetPendingSubSup(true, false);
        else if (newKind == InlineFormatKind.Superscript)
            Editor?.SetPendingSubSup(false, true);
        else
            Editor?.SetPendingSubSup(null, null);
        Dispatcher.UIThread.Post(UpdateToolbarFormatState, DispatcherPriority.Input);
    }

    private void ClearStickySubSup()
    {
        if (_stickySubSup == null) return;
        _stickySubSup = null;
        Editor?.SetPendingSubSup(null, null);
        Dispatcher.UIThread.Post(UpdateToolbarFormatState, DispatcherPriority.Input);
    }

    private static bool IsNavigationKey(Key key) =>
        key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End;

    private async void OnLinkClick(object? sender, RoutedEventArgs e) => await EditLinkAsync();

    private async void OnImageClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (!IsToolbarEnabled)
                return;
            var top = TopLevel.GetTopLevel(this);
            if (top?.StorageProvider == null)
                return;
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("InsertImage", "Flashcards"),
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp", "*.svg" }
                    }
                }
            }).ConfigureAwait(true);
            var first = files.FirstOrDefault();
            if (first == null)
                return;
            var path = first.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (!CanInsertMoreImages())
                return;

            var storedPath = path;
            if (_imageAssetService != null)
            {
                var imageId = Guid.NewGuid().ToString("n");
                var import = await _imageAssetService.ImportAndCopyAsync(path, imageId).ConfigureAwait(true);
                if (import.IsSuccess && !string.IsNullOrWhiteSpace(import.Value))
                    storedPath = import.Value!;
            }

            var defaultAlign = HasBodyText() ? "right" : "center";
            _imageEntries.Add((storedPath, defaultAlign));
            _currentImageIndex = _imageEntries.Count - 1;
            CommitImageChange();
        }
        catch (Exception ex)
        {
            // Guard async void handler from tearing down the UI thread on unexpected import/runtime failures.
            if (_overlayService != null)
            {
                await _overlayService.CreateDialogAsync(
                    T("InsertImage", "Flashcards"),
                    ex.Message,
                    T("OK", "Common"),
                    string.Empty).ConfigureAwait(true);
            }
        }
    }

    // â”€â”€ Image panel handlers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void OnAlignLeftClick(object? sender, RoutedEventArgs e) => ApplyCurrentImageAlign("left");

    private void OnAlignCenterClick(object? sender, RoutedEventArgs e) => ApplyCurrentImageAlign("center");

    private void OnAlignRightClick(object? sender, RoutedEventArgs e) => ApplyCurrentImageAlign("right");

    private void OnDeleteImageClick(object? sender, RoutedEventArgs e)
    {
        if (_currentImageIndex < 0 || _currentImageIndex >= _imageEntries.Count)
            return;
        _imageEntries.RemoveAt(_currentImageIndex);
        _currentImageIndex = Math.Max(0, Math.Min(_currentImageIndex, _imageEntries.Count - 1));
        CommitImageChange();
    }

    private void OnPreviousImageClick(object? sender, RoutedEventArgs e)
    {
        if (_currentImageIndex <= 0)
            return;
        _currentImageIndex--;
        RaiseCarouselProperties();
    }

    private void OnNextImageClick(object? sender, RoutedEventArgs e)
    {
        if (_currentImageIndex >= _embeddedImages.Count - 1)
            return;
        _currentImageIndex++;
        RaiseCarouselProperties();
    }

    private void RaiseCarouselProperties()
    {
        RaisePropertyChanged(nameof(CurrentImage));
        RaisePropertyChanged(nameof(CurrentImagePath));
        RaisePropertyChanged(nameof(IsCurrentImageAlignLeft));
        RaisePropertyChanged(nameof(IsCurrentImageAlignCenter));
        RaisePropertyChanged(nameof(IsCurrentImageAlignRight));
        RaisePropertyChanged(nameof(CanGoToPreviousImage));
        RaisePropertyChanged(nameof(CanGoToNextImage));
        RaisePropertyChanged(nameof(ImageCountLabel));
    }

    private void ApplyCurrentImageAlign(string align)
    {
        if (_currentImageIndex < 0 || _currentImageIndex >= _imageEntries.Count)
            return;
        var (path, _) = _imageEntries[_currentImageIndex];
        _imageEntries[_currentImageIndex] = (path, NormalizeAlign(align));
        CommitImageChange();
    }


    // â”€â”€ Inline formatting â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private void ApplyInlineFormat(InlineFormatKind kind, string? color = null)
    {
        if (!IsToolbarEnabled)
            return;
        var range = GetSelectionRange();
        if (range == null)
        {
            var word = Editor.TryGetWordRangeAtCaret();
            if (word == null)
                return;
            range = (word.Value.Start, word.Value.End);
        }

        ApplyInlineFormatToRange(range.Value.Start, range.Value.End, kind, color, preserveSelection: true);
    }

    private void ApplyInlineFormatToRange(int start, int end, InlineFormatKind kind, string? color, bool preserveSelection)
    {
        // Always operate on the text-only editor spans so caret positions stay valid.
        var editorSpans = Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) };
        var runs = InlineSpanFormatApplier.Apply(editorSpans, start, end, kind, color);
        CommitRuns(runs, end, preserveSelection ? (start, end) : null);
    }

    private (int Start, int End)? GetSelectionRange()
    {
        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        if (start >= end)
            return null;
        return (start, end);
    }

    /// <summary>
    /// Commits <paramref name="textOnlyRuns"/> (text-only spans after a format
    /// operation) to both <see cref="Editor"/> and <see cref="Spans"/> (combined).
    /// </summary>
    private void CommitRuns(IReadOnlyList<InlineSpan> textOnlyRuns, int caret, (int Start, int End)? selection = null)
    {
        var textOnly = InlineSpanFormatApplier.Normalize(textOnlyRuns);
        var combined = BuildCombinedSpans(textOnly);
        _isSyncingFromProperty = true;
        try
        {
            Spans = combined;
            Editor.Spans = textOnly;
            if (selection is { } activeSelection)
            {
                var start = Math.Clamp(activeSelection.Start, 0, Editor.SelectionIndexUpperBound);
                var end = Math.Clamp(activeSelection.End, 0, Editor.SelectionIndexUpperBound);
                Editor.SelectionStart = Math.Min(start, end);
                Editor.SelectionEnd = Math.Max(start, end);
                Editor.CaretIndex = Editor.SelectionEnd;
            }
            else
            {
                var clamped = Math.Clamp(caret, 0, Editor.TextLength);
                Editor.CaretIndex = clamped;
                Editor.SelectionStart = clamped;
                Editor.SelectionEnd = clamped;
            }
            UpdatePreviewMarkdown();
            SyncImageViewItems();
            UpdateToolbarFormatState();
            SpansChanged?.Invoke(Spans);
            CommitHistoryIfChanged("Format text");
        }
        finally
        {
            _isSyncingFromProperty = false;
        }
    }

}
