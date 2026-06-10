using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Mnemo.Core.Models;
using Mnemo.Infrastructure.Services.LaTeX;
using Mnemo.UI.Controls;
using Mnemo.UI.Services;
using Mnemo.UI.Services.LaTeX.Layout.Boxes;
using Mnemo.UI.Services.LaTeX.Rendering;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Owns all inline-equation state for a single <see cref="RichTextEditor"/>:
/// layout box cache, hover state, preview mode, and the edit flyout.
/// Receives the LaTeX engine via constructor injection.
/// </summary>
internal sealed class InlineEquationController
{
    private readonly RichTextEditor _editor;
    private readonly ILaTeXEngine? _engine;
    private readonly LRUCache<(string, double, uint), FormattedText> _mathTextCache = new(200);

    private readonly List<InlineEquationEntry> _entries = new();
    private bool _dirty = true;

    private bool _previewActive;
    private string? _previewLatex;
    private bool _suppressCloseCommit;
    private int _flyoutCharIndex;
    private Rect _flyoutAnchorLocal;
    private int? _hoveredCharIndex;

    private DispatcherTimer? _rebuildTimer;
    private Flyout? _flyout;
    private TextBox? _textBox;
    private Button? _doneButton;

    private static readonly FontFamily MonoFont =
        new("Cascadia Code, Consolas, Courier New, monospace");

    internal InlineEquationController(RichTextEditor editor, ILaTeXEngine? engine)
    {
        _editor = editor;
        _engine = engine;
    }

    internal IReadOnlyList<InlineEquationEntry> Entries => _entries;
    internal bool IsDirty { get => _dirty; set => _dirty = value; }
    internal bool PreviewActive => _previewActive;
    internal string? PreviewLatex => _previewLatex;
    internal int FlyoutCharIndex => _flyoutCharIndex;
    internal int? HoveredCharIndex => _hoveredCharIndex;
    internal LRUCache<(string, double, uint), FormattedText> MathTextCache => _mathTextCache;

    internal void SetHovered(int? charIndex)
    {
        if (_hoveredCharIndex == charIndex) return;
        _hoveredCharIndex = charIndex;
        _editor.InvalidateVisual();
    }

    internal void Detach()
    {
        _rebuildTimer?.Stop();
        _rebuildTimer = null;
    }

    internal async Task RebuildAsync(
        double lastLayoutWidth,
        double boundsWidth,
        double minLayoutWidth,
        double fontSize)
    {
        if (!_dirty) return;
        _dirty = false;

        var perf = EditorPerfDiagnostics.Resolve();
        var perfStart = perf is { IsEnabled: true } ? Stopwatch.GetTimestamp() : 0;

        var lineLayoutCap = lastLayoutWidth > 0 ? lastLayoutWidth : (boundsWidth > 0 ? boundsWidth : minLayoutWidth);

        Dictionary<(string latex, double fs), InlineEquationEntry>? prior = null;
        if (_entries.Count > 0)
        {
            prior = new Dictionary<(string, double), InlineEquationEntry>(_entries.Count);
            foreach (var e in _entries)
            {
                if (e.Layout == null) continue;
                prior[(e.Latex.Trim(), fontSize)] = e;
            }
        }

        var built = new List<InlineEquationEntry>();
        var runs = _editor.Spans ?? Array.Empty<InlineSpan>();
        int charOffset = 0;

        foreach (var seg in runs)
        {
            if (seg is EquationSpan eq && _engine != null)
            {
                var latexForLayout = eq.Latex;
                if (_previewActive && charOffset == _flyoutCharIndex)
                    latexForLayout = _previewLatex ?? latexForLayout;

                var entry = new InlineEquationEntry
                {
                    CharIndex = charOffset,
                    Latex = latexForLayout ?? string.Empty
                };

                var latex = (latexForLayout ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(latex))
                {
                    if (prior != null && prior.TryGetValue((latex, fontSize), out var cached) && cached.Layout is Box cachedBox)
                    {
                        entry.Layout = cachedBox;
                        entry.Width = cached.Width;
                        entry.Height = cached.Height;
                    }
                    else
                    {
                        try
                        {
                            var boxObj = await _engine.GetLayoutBoxAsync(latex, fontSize).ConfigureAwait(true);
                            if (boxObj is Box box)
                            {
                                entry.Layout = box;
                                var advance = box.Width + 2;
                                entry.Width = ClampReserveWidth(advance, latex, fontSize, lineLayoutCap);
                                entry.Height = box.TotalHeight + 2;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Inline equation layout error: {ex.Message}");
                        }
                    }
                }

                built.Add(entry);
                charOffset += 1;
                continue;
            }

            charOffset += seg is TextSpan t ? t.Text.Length : 1;
        }

        _entries.Clear();
        _entries.AddRange(built);

        if (perfStart != 0)
            EditorPerfDiagnostics.RecordIfSlow(perf, "rebuildInlineEquations", EditorPerfDiagnostics.ElapsedMs(perfStart),
                $"{built.Count} equations");
    }

    internal void OpenFlyout(int charIndex, string currentLatex, TextLayout? textLayout, int layoutTextLength)
    {
        _flyoutCharIndex = charIndex;
        _suppressCloseCommit = false;
        _previewActive = true;
        _previewLatex = currentLatex;

        _editor.CaretIndex = charIndex;
        _editor.SelectionStart = charIndex;
        _editor.SelectionEnd = charIndex;

        if (_flyout == null)
        {
            _textBox = new TextBox
            {
                MinWidth = 220, MaxWidth = 360,
                AcceptsReturn = false,
                FontFamily = MonoFont, FontSize = 14,
                PlaceholderText = RichTextThemeBrushes.TNotes("EquationFlyoutPlaceholder")
            };
            _textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) { _suppressCloseCommit = true; _flyout?.Hide(); e.Handled = true; return; }
                if (e.Key == Key.Enter) { _suppressCloseCommit = false; _flyout?.Hide(); e.Handled = true; }
            };
            _textBox.TextChanged += (_, _) =>
            {
                if (_textBox == null || !_previewActive) return;
                _previewLatex = _textBox.Text ?? string.Empty;
                _dirty = true;
                RestartDebounce();
            };

            _doneButton = new Button
            {
                Classes = { "accent" },
                VerticalAlignment = VerticalAlignment.Stretch,
                MinWidth = 80,
                Padding = new Thickness(12, 6),
                Content = $"{RichTextThemeBrushes.TNotes("Done")} \u21B5"
            };
            _doneButton.Click += (_, _) => { _suppressCloseCommit = false; _flyout?.Hide(); };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                MinWidth = 280, MaxWidth = 440
            };
            Grid.SetColumn(_textBox, 0);
            Grid.SetColumn(_doneButton, 1);
            _textBox.Margin = new Thickness(0, 0, 10, 0);
            grid.Children.Add(_textBox);
            grid.Children.Add(_doneButton);

            var shell = new Border
            {
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(8),
                Child = grid
            };
            shell.AttachedToVisualTree += (_, _) =>
            {
                if (shell.TryFindResource("MenuFlyoutPresenterBackground", out var bg) && bg is IBrush bgb)
                    shell.Background = bgb;
                if (shell.TryFindResource("MenuFlyoutPresenterBorderBrush", out var bd) && bd is IBrush bdb)
                    shell.BorderBrush = bdb;
                shell.BorderThickness = new Thickness(1);
            };

            _flyout = new Flyout
            {
                Content = shell,
                Placement = PlacementMode.Custom,
                CustomPopupPlacementCallback = OnPlacement,
                ShowMode = FlyoutShowMode.Standard,
                VerticalOffset = 0, HorizontalOffset = 0
            };
            _flyout.Closed += (_, _) => OnClosed();
            _flyout.Opened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => { _textBox?.Focus(); _textBox?.SelectAll(); }, DispatcherPriority.Input);
            };
        }
        else
            _doneButton!.Content = $"{RichTextThemeBrushes.TNotes("Done")} \u21B5";

        if (textLayout != null)
        {
            try
            {
                var layoutIdx = RichTextLayoutBuilder.LogicalCaretToLayoutBoundary(
                    _editor._layoutBoundaryAtLogical ?? Array.Empty<int>(), charIndex);
                var charRect = textLayout.HitTestTextPosition(layoutIdx);
                double w = RichTextLayoutBuilder.LogicalCaretToLayoutBoundary(
                    _editor._layoutBoundaryAtLogical ?? Array.Empty<int>(), charIndex + 1) - layoutIdx;
                if (w <= 0.5)
                {
                    foreach (var eq in _entries)
                    {
                        if (eq.CharIndex == charIndex && eq.Width > 0) { w = Math.Max(w, eq.Width); break; }
                    }
                    w = Math.Max(w, charRect.Width);
                }
                w = Math.Max(w, 8);
                _flyoutAnchorLocal = new Rect(charRect.X, charRect.Y, w, Math.Max(charRect.Height, 1));
            }
            catch
            {
                _flyoutAnchorLocal = new Rect(0, 0, Math.Max(_editor.Bounds.Width, 8), Math.Max(_editor.Bounds.Height, 1));
            }
        }
        else
            _flyoutAnchorLocal = new Rect(0, 0, Math.Max(_editor.Bounds.Width, 8), Math.Max(_editor.Bounds.Height, 1));

        _textBox!.Text = currentLatex;
        _editor.InvalidateVisual();
        _flyout.ShowAt(_editor);
    }

    private void OnClosed()
    {
        _previewActive = false;
        _rebuildTimer?.Stop();
        var commit = !_suppressCloseCommit;
        _suppressCloseCommit = false;
        if (commit)
            _editor.RaiseInlineEquationEdited(_flyoutCharIndex, _textBox?.Text ?? string.Empty);
        _previewLatex = null;
        _dirty = true;
        _editor.InvalidateVisual();
        _ = _editor.RebuildInlineEquationsAsync();
    }

    private void OnPlacement(CustomPopupPlacement p)
    {
        if (p.Target is not Visual v) return;
        var top = TopLevel.GetTopLevel(v);
        if (top == null) return;
        var m = v.TransformToVisual(top);
        if (m == null) return;
        p.AnchorRectangle = _flyoutAnchorLocal.TransformToAABB(m.Value);
        p.Anchor = PopupAnchor.Bottom;
        p.Gravity = PopupGravity.Bottom;
        p.Offset = new Point(0, 8);
        p.ConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideY;
    }

    private void RestartDebounce()
    {
        if (_rebuildTimer == null)
        {
            _rebuildTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            _rebuildTimer.Tick += (_, _) =>
            {
                _rebuildTimer?.Stop();
                if (!_previewActive) return;
                _dirty = true;
                _ = _editor.RebuildInlineEquationsAsync();
            };
        }
        _rebuildTimer.Stop();
        _rebuildTimer.Start();
    }

    /// <summary>Clamp over-sized equation reserve widths to a plausible bound based on source length.</summary>
    internal static double ClampReserveWidth(double advance, string latex, double fontSize, double lineMaxWidth)
    {
        if (advance <= 0 || double.IsNaN(advance) || double.IsInfinity(advance))
            return Math.Max(fontSize * 0.5, 1);

        var t = (latex ?? string.Empty).Trim();
        if (t.Length == 0) return advance;
        if (lineMaxWidth > 0 && advance > lineMaxWidth) advance = lineMaxWidth;
        if (t.Length > 96) return advance;

        double lengthSlack = t.Length * fontSize * 0.82 + fontSize * 12;
        if (advance <= lengthSlack * 1.45) return advance;
        return Math.Min(advance, lengthSlack);
    }

    /// <summary>Union of box ink bounds (same convention as <see cref="LaTeXRenderer"/>).</summary>
    internal static Rect CalculateMathPaintBounds(Box box, double x, double baselineY)
    {
        var top = baselineY - box.Height;
        var bounds = new Rect(x, top, box.Width, box.TotalHeight);
        foreach (var (child, cx, cy) in box.GetChildPositions(x, baselineY))
            bounds = bounds.Union(CalculateMathPaintBounds(child, cx, cy));
        return bounds;
    }
}
