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
    // â”€â”€ Image layer helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    /// <summary>
    /// Scans <paramref name="spans"/> for embedded image tokens and populates
    /// <see cref="_imageEntries"/>.  Resets <see cref="_currentImageIndex"/> to 0.
    /// </summary>
    private void ExtractImageEntries(IReadOnlyList<InlineSpan> spans)
    {
        _imageEntries.Clear();
        var flat = InlineSpanFormatApplier.Flatten(spans);
        foreach (Match m in EmbeddedImageRegex.Matches(flat))
        {
            var path = m.Groups["path"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(path))
                _imageEntries.Add((path, NormalizeAlign(m.Groups["align"].Value)));
        }
        _currentImageIndex = 0;
    }

    /// <summary>
    /// Returns a copy of <paramref name="spans"/> with all embedded image tokens
    /// stripped so the inner <see cref="RichTextEditor"/> only shows text.
    /// </summary>
    private static IReadOnlyList<InlineSpan> BuildTextOnlySpans(IReadOnlyList<InlineSpan> spans)
    {
        var flat = InlineSpanFormatApplier.Flatten(spans);
        if (!EmbeddedImageRegex.IsMatch(flat))
            return spans;

        var stripped = StripImageTokensFromText(flat);
        if (string.Equals(stripped, flat, StringComparison.Ordinal))
            return spans;

        return InlineSpanFormatApplier.Normalize(
            InlineSpanFormatApplier.ApplyTextEdit(spans, flat, stripped));
    }

    /// <summary>
    /// Merges <paramref name="textOnlySpans"/> with the current
    /// <see cref="_imageEntries"/> to produce the canonical combined payload
    /// stored in <see cref="Spans"/>.
    /// </summary>
    private IReadOnlyList<InlineSpan> BuildCombinedSpans(IReadOnlyList<InlineSpan> textOnlySpans)
    {
        if (_imageEntries.Count == 0)
            return InlineSpanFormatApplier.Normalize(new List<InlineSpan>(textOnlySpans));

        var imageTokenText = new StringBuilder();
        foreach (var (path, align) in _imageEntries)
            imageTokenText.Append($"![]({path}){{align={align}}}");

        var combined = new List<InlineSpan>(textOnlySpans) { InlineSpan.Plain(imageTokenText.ToString()) };
        return InlineSpanFormatApplier.Normalize(combined);
    }

    /// <summary>
    /// Rebuilds <see cref="Spans"/> from the current editor text +
    /// <see cref="_imageEntries"/>, then fires <see cref="SpansChanged"/>.
    /// </summary>
    private void CommitImageChange()
    {
        var textOnly = Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) };
        var combined = BuildCombinedSpans(textOnly);
        _isSyncingFromProperty = true;
        try { Spans = combined; }
        finally { _isSyncingFromProperty = false; }

        UpdatePreviewMarkdown();
        SyncImageViewItems();
        RefreshCanInsertImage();
        SpansChanged?.Invoke(Spans);
        CommitHistoryIfChanged("Edit image");
    }

    private static string StripImageTokensFromText(string text) =>
        // Preserve user-entered whitespace exactly; only strip image tokens.
        EmbeddedImageRegex.Replace(text, string.Empty);

    private bool HasBodyText()
    {
        var flat = InlineSpanFormatApplier.Flatten(
            Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) });
        return !string.IsNullOrWhiteSpace(flat);
    }

    private void SyncImageViewItems()
    {
        _embeddedImages.Clear();
        for (var i = 0; i < _imageEntries.Count; i++)
        {
            var (path, align) = _imageEntries[i];
            _embeddedImages.Add(new RichDocumentEmbeddedImageViewItem
            {
                Index = i,
                Path = path,
                Align = align
            });
        }

        if (_embeddedImages.Count > 0 && _currentImageIndex >= _embeddedImages.Count)
            _currentImageIndex = _embeddedImages.Count - 1;

        RaisePropertyChanged(nameof(EmbeddedImages));
        RaisePropertyChanged(nameof(HasEmbeddedImages));
        RaisePropertyChanged(nameof(CurrentImage));
        RaisePropertyChanged(nameof(HasImages));
        RaisePropertyChanged(nameof(HasMultipleImages));
        RaiseCarouselProperties();
    }

    private bool CanInsertMoreImages()
    {
        var maxImages = HasBodyText() ? 1 : 2;
        return _imageEntries.Count < maxImages;
    }

    private void RefreshCanInsertImage()
    {
        var next = IsToolbarEnabled && CanInsertMoreImages();
        if (next == CanInsertImage)
            return;
        CanInsertImage = next;
        RaisePropertyChanged(nameof(CanInsertImage));
    }

    private static string NormalizeAlign(string? align) =>
        align?.Trim().ToLowerInvariant() switch
        {
            "left" => "left",
            "right" => "right",
            _ => "center"
        };

    // â”€â”€ Link editing â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task EditLinkAsync()
    {
        if (!IsToolbarEnabled || _overlayService == null)
            return;
        var range = GetSelectionRange();
        if (range == null)
        {
            var word = Editor.TryGetWordRangeAtCaret();
            if (word == null)
                return;
            Editor.SelectionStart = word.Value.Start;
            Editor.SelectionEnd = word.Value.End;
            Editor.CaretIndex = word.Value.End;
            range = (word.Value.Start, word.Value.End);
        }

        if (range == null)
            return;
        var (start, end) = range.Value;

        // Use Editor.Spans (text-only) so caret positions align correctly.
        var editorSpans = Editor.Spans ?? new List<InlineSpan> { InlineSpan.Plain(string.Empty) };
        var flat = InlineSpanFormatApplier.Flatten(editorSpans);
        if (end > flat.Length || end <= start)
            return;

        var selectedText = flat[start..end];
        var initialUrl = GetLinkUrlForRange(editorSpans, start, end) ?? string.Empty;
        var result = await ShowLinkDialogAsync(initialUrl, selectedText).ConfigureAwait(true);
        if (result == null)
            return;

        if (result.RemoveLinkRequested)
        {
            ApplyInlineFormatToRange(start, end, InlineFormatKind.Link, null, preserveSelection: true);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.Url))
            return;

        var normalized = NormalizeUrl(result.Url);
        var replacedDisplay = string.IsNullOrWhiteSpace(result.DisplayText) ? selectedText : result.DisplayText.Trim();
        var currentFlat = InlineSpanFormatApplier.Flatten(editorSpans);
        if (start > currentFlat.Length)
            return;
        var replaceEnd = Math.Clamp(end, start, currentFlat.Length);
        var newFlat = currentFlat[..start] + replacedDisplay + currentFlat[replaceEnd..];
        var runs = InlineSpanFormatApplier.ApplyTextEdit(editorSpans, currentFlat, newFlat);
        var linkEnd = start + replacedDisplay.Length;
        runs = InlineSpanFormatApplier.Apply(runs, start, linkEnd, InlineFormatKind.Link, normalized);
        CommitRuns(runs, linkEnd);
    }

    private Task<LinkEditDialogResult?> ShowLinkDialogAsync(string url, string displayText)
    {
        if (_overlayService == null)
            return Task.FromResult<LinkEditDialogResult?>(null);

        var tcs = new TaskCompletionSource<LinkEditDialogResult?>();
        var dialog = new LinkInsertDialogOverlay
        {
            Title = T("InsertLinkTitle", "NotesEditor"),
            UrlLabel = T("InsertLinkUrlLabel", "NotesEditor"),
            Url = url,
            UrlPlaceholder = T("InsertLinkUrlPlaceholder", "NotesEditor"),
            DisplayLabel = T("InsertLinkDisplayLabel", "NotesEditor"),
            DisplayText = displayText,
            DisplayPlaceholder = T("InsertLinkDisplayPlaceholder", "NotesEditor"),
            ShowDisplaySection = true,
            ShowCrossBlockHint = false,
            ShowRemoveLink = !string.IsNullOrWhiteSpace(url),
            RemoveLinkText = T("InsertLinkRemoveLink", "NotesEditor"),
            ConfirmText = T("OK", "Common"),
            CancelText = T("Cancel", "Common"),
            RequireUrlForConfirm = false
        };

        var overlayId = _overlayService.CreateOverlay(dialog, new OverlayOptions
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            ShowBackdrop = true,
            CloseOnOutsideClick = true,
            CloseOnEscape = true
        }, "RichDocumentLinkEdit");

        dialog.OnResult = result =>
        {
            _overlayService.CloseOverlay(overlayId);
            tcs.TrySetResult(result);
        };

        return tcs.Task;
    }

    private static string? GetLinkUrlForRange(IReadOnlyList<InlineSpan> spans, int start, int end)
    {
        int offset = 0;
        foreach (var span in spans)
        {
            var runLength = span is TextSpan t ? t.Text.Length : 1;
            var runEnd = offset + runLength;
            if (runEnd > start && offset < end)
            {
                var url = span switch
                {
                    TextSpan textSpan => textSpan.Style.LinkUrl,
                    EquationSpan equationSpan => equationSpan.Style.LinkUrl,
                    FractionSpan fractionSpan => fractionSpan.Style.LinkUrl,
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            offset = runEnd;
        }

        return null;
    }

    private static string NormalizeUrl(string url)
    {
        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var abs))
            return abs.ToString();
        if (trimmed.Contains('@', StringComparison.Ordinal) && !trimmed.Contains("://", StringComparison.Ordinal))
            return "mailto:" + trimmed;
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            return "https://" + trimmed;
        return trimmed;
    }

}
