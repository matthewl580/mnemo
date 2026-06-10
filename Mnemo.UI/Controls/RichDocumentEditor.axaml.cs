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

/// <summary>
/// Reusable rich-document editor with a fixed toolbar and preview mode.
/// Images are managed as a separate layer from inline text: the inner
/// <see cref="RichTextEditor"/> only ever sees text-only spans, while image
/// metadata lives in <see cref="_imageEntries"/> and is re-injected into
/// <see cref="Spans"/> (the persisted/preview payload) transparently.
/// </summary>
public partial class RichDocumentEditor : UserControl, INotifyPropertyChanged
{
    private static readonly Regex EmbeddedImageRegex = new(
        @"!\[(?<alt>[^\]]*)\]\((?<path>[^)]+)\)(?:\{align=(?<align>left|center|right)\})?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public new event PropertyChangedEventHandler? PropertyChanged;
    public event Action<IReadOnlyList<InlineSpan>>? SpansChanged;

    public static readonly StyledProperty<IReadOnlyList<InlineSpan>> SpansProperty =
        AvaloniaProperty.Register<RichDocumentEditor, IReadOnlyList<InlineSpan>>(
            nameof(Spans),
            defaultValue: new List<InlineSpan> { InlineSpan.Plain(string.Empty) },
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<RichDocumentEditor, string?>(nameof(Watermark));

    public static readonly StyledProperty<bool> IsReadOnlyProperty =
        AvaloniaProperty.Register<RichDocumentEditor, bool>(nameof(IsReadOnly));

    public static readonly StyledProperty<bool> IsPreviewModeProperty =
        AvaloniaProperty.Register<RichDocumentEditor, bool>(nameof(IsPreviewMode));

    public static readonly StyledProperty<bool> ShowModeToggleProperty =
        AvaloniaProperty.Register<RichDocumentEditor, bool>(nameof(ShowModeToggle), defaultValue: true);

    public static readonly StyledProperty<bool> ShowImageButtonProperty =
        AvaloniaProperty.Register<RichDocumentEditor, bool>(nameof(ShowImageButton), defaultValue: true);

    public static readonly StyledProperty<string> WriteLabelProperty =
        AvaloniaProperty.Register<RichDocumentEditor, string>(nameof(WriteLabel), defaultValue: "Write");

    public static readonly StyledProperty<string> PreviewLabelProperty =
        AvaloniaProperty.Register<RichDocumentEditor, string>(nameof(PreviewLabel), defaultValue: "Preview");

    public IReadOnlyList<InlineSpan> Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public bool IsReadOnly
    {
        get => GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public bool IsPreviewMode
    {
        get => GetValue(IsPreviewModeProperty);
        set => SetValue(IsPreviewModeProperty, value);
    }

    public bool ShowModeToggle
    {
        get => GetValue(ShowModeToggleProperty);
        set => SetValue(ShowModeToggleProperty, value);
    }

    public bool ShowImageButton
    {
        get => GetValue(ShowImageButtonProperty);
        set => SetValue(ShowImageButtonProperty, value);
    }

    public string WriteLabel
    {
        get => GetValue(WriteLabelProperty);
        set => SetValue(WriteLabelProperty, value);
    }

    public string PreviewLabel
    {
        get => GetValue(PreviewLabelProperty);
        set => SetValue(PreviewLabelProperty, value);
    }

    public bool IsToolbarEnabled => !IsReadOnly && !IsPreviewMode;
    public bool IsBoldActive { get; private set; }
    public bool IsItalicActive { get; private set; }
    public bool IsUnderlineActive { get; private set; }
    public bool IsStrikethroughActive { get; private set; }
    public bool IsHighlightActive { get; private set; }
    public bool IsSubscriptActive { get; private set; }
    public bool IsSuperscriptActive { get; private set; }

    private InlineFormatKind? _stickySubSup;

    public bool CanInsertImage { get; private set; } = true;

    public string PreviewMarkdown { get; private set; } = string.Empty;

    // â”€â”€ Image layer â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    // Source of truth for images: populated from Spans on load, updated by
    // image operations, and re-serialised back into Spans by BuildCombinedSpans.

    private readonly List<(string Path, string Align)> _imageEntries = new();
    private readonly List<RichDocumentEmbeddedImageViewItem> _embeddedImages = new();
    private int _currentImageIndex;

    /// <summary>Currently displayed image in the carousel, or null when there are none.</summary>
    public RichDocumentEmbeddedImageViewItem? CurrentImage =>
        _embeddedImages.Count > 0
            ? _embeddedImages[Math.Clamp(_currentImageIndex, 0, _embeddedImages.Count - 1)]
            : null;

    public string? CurrentImagePath => CurrentImage?.Path;
    public bool IsCurrentImageAlignLeft => CurrentImage?.IsAlignLeft == true;
    public bool IsCurrentImageAlignCenter => CurrentImage?.IsAlignCenter == true;
    public bool IsCurrentImageAlignRight => CurrentImage?.IsAlignRight == true;

    public bool HasImages => _embeddedImages.Count > 0;
    public bool HasMultipleImages => _embeddedImages.Count > 1;
    public bool CanGoToPreviousImage => _currentImageIndex > 0 && HasImages;
    public bool CanGoToNextImage => _currentImageIndex < _embeddedImages.Count - 1;
    public string ImageCountLabel => HasMultipleImages ? $"{_currentImageIndex + 1} / {_embeddedImages.Count}" : string.Empty;

    // Legacy surface kept for any external consumers.
    public IReadOnlyList<RichDocumentEmbeddedImageViewItem> EmbeddedImages => _embeddedImages;
    public bool HasEmbeddedImages => _embeddedImages.Count > 0;

    // â”€â”€ Internal state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private bool _isSyncingFromProperty;
    private bool _isHandlingEditorTextChanged;
    private bool _isApplyingHistory;
    private IHistoryManager? _history;
    private EditorSnapshot? _historyBaseline;
    private readonly IOverlayService? _overlayService;
    private readonly ILocalizationService? _localization;
    private readonly IImageAssetService? _imageAssetService;

    public RichDocumentEditor()
    {
        InitializeComponent();
        var services = (Application.Current as App)?.Services;
        _overlayService = services?.GetService<IOverlayService>();
        _localization = services?.GetService<ILocalizationService>();
        _imageAssetService = services?.GetService<IImageAssetService>();
        _history = services?.GetService<IHistoryManager>();
        UpdatePreviewMarkdown();
        SyncImageViewItems();
        UpdateModeButtonClasses();
        _historyBaseline = CaptureSnapshot();
    }

    /// <summary>
    /// Optional document-scoped history manager. When attached, the editor records
    /// text and image changes so Ctrl+Z / Ctrl+Y can undo and redo.
    /// </summary>
    public IHistoryManager? History
    {
        get => _history;
        set => _history = value;
    }

    /// <summary>
    /// Convenience API for host views that attach history manager imperatively.
    /// </summary>
    public void AttachHistoryManager(IHistoryManager? historyManager) => History = historyManager;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateModeButtonClasses();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpansProperty)
        {
            if (!_isSyncingFromProperty)
            {
                ExtractImageEntries(Spans);
                var textOnly = BuildTextOnlySpans(Spans);
                _isSyncingFromProperty = true;
                try { Editor.Spans = textOnly; }
                finally { _isSyncingFromProperty = false; }
                UpdatePreviewMarkdown();
                SyncImageViewItems();
                UpdateToolbarFormatState();
            }
        }
        else if (change.Property == IsPreviewModeProperty || change.Property == IsReadOnlyProperty)
        {
            RaisePropertyChanged(nameof(IsToolbarEnabled));
            RefreshCanInsertImage();
            UpdateModeButtonClasses();
            UpdateToolbarFormatState();
        }
    }
    private static bool SpansEquivalent(IReadOnlyList<InlineSpan>? left, IReadOnlyList<InlineSpan>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            var l = left[i];
            var r = right[i];
            if (l.GetType() != r.GetType())
                return false;

            switch (l)
            {
                case TextSpan lt when r is TextSpan rt:
                    if (!string.Equals(lt.Text, rt.Text, StringComparison.Ordinal)
                        || !Equals(lt.Style, rt.Style))
                        return false;
                    break;
                case EquationSpan le when r is EquationSpan re:
                    if (!string.Equals(le.Latex, re.Latex, StringComparison.Ordinal)
                        || !Equals(le.Style, re.Style))
                        return false;
                    break;
                case FractionSpan lf when r is FractionSpan rf:
                    if (lf.Numerator != rf.Numerator
                        || lf.Denominator != rf.Denominator
                        || !Equals(lf.Style, rf.Style))
                        return false;
                    break;
                default:
                    return false;
            }
        }

        return true;
    }

    private EditorSnapshot CaptureSnapshot()
    {
        var normalized = InlineSpanFormatApplier.Normalize(
            Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
        return new EditorSnapshot(
            normalized,
            Editor?.CaretIndex ?? 0,
            Editor?.SelectionStart ?? 0,
            Editor?.SelectionEnd ?? 0);
    }

    private void ApplySnapshot(EditorSnapshot snapshot)
    {
        _isApplyingHistory = true;
        try
        {
            LoadSpans(snapshot.Spans);
            var caret = Math.Clamp(snapshot.CaretIndex, 0, Editor.TextLength);
            var start = Math.Clamp(snapshot.SelectionStart, 0, Editor.SelectionIndexUpperBound);
            var end = Math.Clamp(snapshot.SelectionEnd, 0, Editor.SelectionIndexUpperBound);
            Editor.CaretIndex = caret;
            Editor.SelectionStart = start;
            Editor.SelectionEnd = end;
            UpdateToolbarFormatState();
        }
        finally
        {
            _isApplyingHistory = false;
        }
    }

    private void CommitHistoryIfChanged(string description)
    {
        if (_isApplyingHistory || History == null)
        {
            _historyBaseline = CaptureSnapshot();
            return;
        }

        var current = CaptureSnapshot();
        var before = _historyBaseline ?? current;
        if (!SnapshotsEquivalent(before, current))
        {
            History.Push(new RichDocumentEditOperation(
                description,
                before,
                current,
                ApplySnapshot));
        }
        _historyBaseline = current;
    }

    private static bool SnapshotsEquivalent(EditorSnapshot left, EditorSnapshot right) =>
        left.CaretIndex == right.CaretIndex
        && left.SelectionStart == right.SelectionStart
        && left.SelectionEnd == right.SelectionEnd
        && SpansEquivalent(left.Spans, right.Spans);

    private async Task CopySelectionAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        if (start >= end || end > Editor.Text.Length)
            return;

        await clipboard.SetTextAsync(Editor.Text[start..end]).ConfigureAwait(true);
    }

    private async Task CutSelectionAsync()
    {
        if (IsReadOnly)
            return;

        var start = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var end = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);
        if (start >= end || end > Editor.Text.Length)
            return;

        await CopySelectionAsync().ConfigureAwait(true);
        Editor.InsertTextAtCaret(string.Empty);
    }

    private async Task PasteSelectionAsync()
    {
        if (IsReadOnly)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null)
            return;

        var text = await clipboard.TryGetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrEmpty(text))
            return;

        Editor.InsertTextAtCaret(text);
    }

    private async Task UndoAsync()
    {
        if (History == null || !History.CanUndo)
            return;

        await History.UndoAsync().ConfigureAwait(true);
        _historyBaseline = CaptureSnapshot();
    }

    private async Task RedoAsync()
    {
        if (History == null || !History.CanRedo)
            return;

        await History.RedoAsync().ConfigureAwait(true);
        _historyBaseline = CaptureSnapshot();
    }

    private void UpdateToolbarFormatState()
    {
        if (Editor == null || IsPreviewMode)
        {
            SetToolbarFormatState(false, false, false, false, false);
            return;
        }

        var textOnlySpans = Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) };
        var selectionStart = Math.Min(Editor.SelectionStart, Editor.SelectionEnd);
        var selectionEnd = Math.Max(Editor.SelectionStart, Editor.SelectionEnd);

        if (selectionEnd > selectionStart)
        {
            var selectedState = GetFormatStateForRange(textOnlySpans, selectionStart, selectionEnd);
            bool subSel = _stickySubSup == InlineFormatKind.Subscript || selectedState.Subscript;
            bool supSel = _stickySubSup == InlineFormatKind.Superscript || selectedState.Superscript;
            SetToolbarFormatState(selectedState.Bold, selectedState.Italic, selectedState.Underline, selectedState.Strikethrough, selectedState.Highlight, subSel, supSel);
            return;
        }

        var caretState = GetFormatStateAtCaret(textOnlySpans, Math.Clamp(Editor.CaretIndex, 0, Editor.TextLength));
        bool subCaret = _stickySubSup == InlineFormatKind.Subscript || caretState.Subscript;
        bool supCaret = _stickySubSup == InlineFormatKind.Superscript || caretState.Superscript;
        SetToolbarFormatState(caretState.Bold, caretState.Italic, caretState.Underline, caretState.Strikethrough, caretState.Highlight, subCaret, supCaret);
    }

    private static (bool Bold, bool Italic, bool Underline, bool Strikethrough, bool Highlight, bool Subscript, bool Superscript) GetFormatStateAtCaret(
        IReadOnlyList<InlineSpan> runs,
        int caretIndex)
    {
        var totalLength = InlineSpanFormatApplier.Flatten(runs).Length;
        if (totalLength == 0)
            return (false, false, false, false, false, false, false);

        var start = Math.Clamp(caretIndex - 1, 0, Math.Max(0, totalLength - 1));
        var end = Math.Clamp(start + 1, 0, totalLength);
        if (end <= start)
            return (false, false, false, false, false, false, false);

        return GetFormatStateForRange(runs, start, end);
    }

    private static (bool Bold, bool Italic, bool Underline, bool Strikethrough, bool Highlight, bool Subscript, bool Superscript) GetFormatStateForRange(
        IReadOnlyList<InlineSpan> runs,
        int start,
        int end)
    {
        if (runs.Count == 0 || start >= end)
            return (false, false, false, false, false, false, false);

        var bold = true;
        var italic = true;
        var underline = true;
        var strikethrough = true;
        var highlight = true;
        var subscript = true;
        var superscript = true;
        var anyOverlap = false;
        var pos = 0;

        foreach (var span in runs)
        {
            var spanLength = span is TextSpan textSpan ? textSpan.Text.Length : 1;
            var spanEnd = pos + spanLength;
            if (spanEnd <= start || pos >= end)
            {
                pos = spanEnd;
                continue;
            }

            if (span is not TextSpan run)
            {
                pos = spanEnd;
                continue;
            }

            anyOverlap = true;
            if (!run.Style.Bold)
                bold = false;
            if (!run.Style.Italic)
                italic = false;
            if (!run.Style.Underline)
                underline = false;
            if (!run.Style.Strikethrough)
                strikethrough = false;
            if (!run.Style.Highlight)
                highlight = false;
            if (!run.Style.Subscript)
                subscript = false;
            if (!run.Style.Superscript)
                superscript = false;

            pos = spanEnd;
        }

        if (!anyOverlap)
            return (false, false, false, false, false, false, false);

        return (bold, italic, underline, strikethrough, highlight, subscript, superscript);
    }

    private void SetToolbarFormatState(bool bold, bool italic, bool underline, bool strikethrough, bool highlight, bool subscript = false, bool superscript = false)
    {
        if (IsBoldActive != bold)
        {
            IsBoldActive = bold;
            RaisePropertyChanged(nameof(IsBoldActive));
        }

        if (IsItalicActive != italic)
        {
            IsItalicActive = italic;
            RaisePropertyChanged(nameof(IsItalicActive));
        }

        if (IsUnderlineActive != underline)
        {
            IsUnderlineActive = underline;
            RaisePropertyChanged(nameof(IsUnderlineActive));
        }

        if (IsStrikethroughActive != strikethrough)
        {
            IsStrikethroughActive = strikethrough;
            RaisePropertyChanged(nameof(IsStrikethroughActive));
        }

        if (IsHighlightActive != highlight)
        {
            IsHighlightActive = highlight;
            RaisePropertyChanged(nameof(IsHighlightActive));
        }

        if (IsSubscriptActive != subscript)
        {
            IsSubscriptActive = subscript;
            RaisePropertyChanged(nameof(IsSubscriptActive));
        }

        if (IsSuperscriptActive != superscript)
        {
            IsSuperscriptActive = superscript;
            RaisePropertyChanged(nameof(IsSuperscriptActive));
        }
    }

    private sealed record EditorSnapshot(
        IReadOnlyList<InlineSpan> Spans,
        int CaretIndex,
        int SelectionStart,
        int SelectionEnd);

    private sealed class RichDocumentEditOperation : IHistoryOperation
    {
        private readonly EditorSnapshot _before;
        private readonly EditorSnapshot _after;
        private readonly Action<EditorSnapshot> _apply;

        public RichDocumentEditOperation(
            string description,
            EditorSnapshot before,
            EditorSnapshot after,
            Action<EditorSnapshot> apply)
        {
            Description = description;
            _before = before;
            _after = after;
            _apply = apply;
        }

        public string Description { get; }
        public OperationSource Source => OperationSource.NotesEditor;

        public Task ApplyAsync()
        {
            _apply(_after);
            return Task.CompletedTask;
        }

        public Task RollbackAsync()
        {
            _apply(_before);
            return Task.CompletedTask;
        }
    }
}
