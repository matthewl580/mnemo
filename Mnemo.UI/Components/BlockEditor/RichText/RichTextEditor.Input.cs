using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using Avalonia.Threading;
using Avalonia.Layout;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives.PopupPositioning;
using Microsoft.Extensions.DependencyInjection;
using Mnemo.Core.Formatting;
using Mnemo.Core.Models;
using Mnemo.Core.Services;
using Mnemo.Infrastructure.Services.LaTeX;
using Mnemo.Infrastructure.Services.TextShortcuts;
using Mnemo.UI.Controls;
using Mnemo.UI;
using Mnemo.UI.Services;
using Mnemo.UI.Services.LaTeX.Layout.Boxes;
using Mnemo.UI.Services.LaTeX.Rendering;

namespace Mnemo.UI.Components.BlockEditor;

public partial class RichTextEditor
{
    // ── Pointer input ────────────────────────────────────────────────────────

    /// <summary>
    /// Programmatically begins a drag-select from outside a pointer event (e.g. when the press
    /// landed in the block padding rather than directly over this control). Captures the pointer
    /// so subsequent PointerMoved events update the selection normally.
    /// </summary>
    public void StartDragSelect(int anchorCharIndex, IPointer pointer)
    {
        Focus();
        anchorCharIndex = Math.Clamp(anchorCharIndex, 0, TextLength);
        CaretIndex = anchorCharIndex;
        SelectionStart = anchorCharIndex;
        SelectionEnd = anchorCharIndex;
        _dragAnchor = anchorCharIndex;
        _isDragging = true;
        pointer.Capture(this);
        ResetCaretBlink();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(Watermark))
            InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _equations?.SetHovered(null);
        if (string.IsNullOrEmpty(Text) && !string.IsNullOrEmpty(Watermark))
            InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            if (TryOpenSpellcheckContextMenu(e.GetPosition(this)))
            {
                e.Handled = true;
                return;
            }
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var pos = e.GetPosition(this);
        var idx = HitTestPoint(pos);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && TryGetLinkUrlAt(idx, out var linkUrl)
            && !string.IsNullOrEmpty(linkUrl))
        {
            e.Handled = true;
            if (ExternalLinkNavigationRequested != null)
                _ = ExternalLinkNavigationRequested.Invoke(linkUrl);
            else
                OpenExternalUrl(linkUrl, this);
            return;
        }

        Focus();

        if (e.ClickCount == 1)
        {
            // Single-click must place the caret / start a drag-select. Opening the equation editor here
            // stole the gesture and made selection across inline math unreliable (double-click still opens).
            CaretIndex = idx;
            SelectionStart = idx;
            SelectionEnd = idx;
            _dragAnchor = idx;
            _isDragging = true;
            e.Pointer.Capture(this);
        }
        else if (e.ClickCount == 2)
        {
            if (!IsReadOnly && TryOpenInlineEquationFlyoutAtPoint(pos))
            {
                e.Handled = true;
                return;
            }
            SelectWord(idx);
        }
        else if (e.ClickCount >= 3)
        {
            SelectionStart = 0;
            SelectionEnd = TextLength;
            CaretIndex = TextLength;
        }

        ResetCaretBlink();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        base.OnPointerMoved(e);
        UpdateInlineEquationHoverState(e.GetPosition(this));
        var isLeftPressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (!isLeftPressed)
        {
            // If the left button was released while another control (e.g. BlockEditor for cross-block
            // selection) held pointer capture, OnPointerReleased was never called here, so _isDragging
            // could still be true. Reset it now to prevent stale drag-select from continuing after
            // the button is released.
            if (_isDragging)
                _isDragging = false;
            if (perfStart != 0)
            {
                EditorPerfDiagnostics.ReportInteraction(
                    perf,
                    "richText.pointerMoved.hover",
                    EditorPerfDiagnostics.ElapsedMs(perfStart),
                    $"text={TextLength}");
            }
            return;
        }
        if (!_isDragging)
        {
            _dragAnchor = CaretIndex;
            _isDragging = true;
            e.Pointer.Capture(this);
        }

        var pos = e.GetPosition(this);
        var idx = HitTestPoint(pos);
        SelectionStart = Math.Min(_dragAnchor, idx);
        SelectionEnd = Math.Max(_dragAnchor, idx);
        CaretIndex = idx;
        ResetCaretBlink();

        if (perfStart != 0)
        {
            EditorPerfDiagnostics.ReportInteraction(
                perf,
                "richText.pointerMoved.drag",
                EditorPerfDiagnostics.ElapsedMs(perfStart),
                $"text={TextLength} anchor={_dragAnchor} idx={idx}");
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            e.Pointer.Capture(null);
        }
    }


    // ── Keyboard input ───────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Left:
                MoveOrExtend(shift, ctrl
                    ? FindWordStart(_caretIndex - 1)
                    : Math.Max(0, _caretIndex - 1));
                e.Handled = true;
                break;

            case Key.Right:
                MoveOrExtend(shift, ctrl
                    ? FindWordEnd(_caretIndex + 1)
                    : Math.Min(TextLength, _caretIndex + 1));
                e.Handled = true;
                break;

            case Key.Home:
                MoveOrExtend(shift, 0);
                e.Handled = true;
                break;

            case Key.End:
                MoveOrExtend(shift, TextLength);
                e.Handled = true;
                break;

            case Key.Delete:
                if (!IsReadOnly)
                    HandleDelete();
                e.Handled = true;
                break;

            case Key.Back:
                // Let parent tunnel handler deal with merge/block-delete; only handle
                // in-block backspace (caret not at 0, or active selection).
                if (_caretIndex > 0 || HasSelection)
                {
                    if (!IsReadOnly)
                    {
                        if (ctrl && !HasSelection)
                            HandleBackspaceWord();
                        else
                            HandleBackspace();
                    }
                    e.Handled = true;
                }
                break;

            case Key.A when ctrl:
                if (Math.Min(SelectionStart, SelectionEnd) == 0 && Math.Max(SelectionStart, SelectionEnd) == TextLength)
                {
                    // Full text already selected, let parent (BlockEditor) handle it to select all blocks
                    e.Handled = false;
                    break;
                }
                SelectionStart = 0;
                SelectionEnd = TextLength;
                CaretIndex = TextLength;
                e.Handled = true;
                break;

            // Ctrl+C / Ctrl+X / Ctrl+V: owned by BlockEditor tunnel handler (markdown + Mnemo JSON).
        }

        if (e.Handled)
            ResetCaretBlink();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (IsReadOnly || string.IsNullOrEmpty(e.Text)) return;

        InsertText(e.Text);
        e.Handled = true;
        ResetCaretBlink();
    }

}