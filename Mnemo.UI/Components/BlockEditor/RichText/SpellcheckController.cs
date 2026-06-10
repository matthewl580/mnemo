using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mnemo.Core.Models;
using Mnemo.Core.Services;

namespace Mnemo.UI.Components.BlockEditor;

/// <summary>
/// Owns all spellcheck state for a single <see cref="RichTextEditor"/> instance:
/// debounce timer, issue list, underline geometry, context menu, and setting subscriptions.
/// Receives services via constructor injection; falls back to no-op when services are null
/// (design-time, unit-test, or read-only contexts).
/// </summary>
internal sealed class SpellcheckController
{
    private readonly RichTextEditor _editor;
    private readonly ISpellcheckService? _service;
    private readonly ISettingsService? _settings;

    private readonly object _sync = new();
    private readonly List<SpellcheckIssue> _issues = [];

    private DispatcherTimer? _debounceTimer;
    private CancellationTokenSource? _cts;
    private bool _enabled = true;
    private List<string> _languages = ["en"];
    private bool _initialized;
    private string _lastCheckedText = string.Empty;
    private int _activeCheckId;

    private List<(Point From, Point To)>? _underlineLines;
    private Pen? _pen;
    private IBrush? _brush;

    private static readonly string[] SettingKeys = ["Editor.SpellCheck", "Editor.SpellCheckLanguages"];

    internal SpellcheckController(RichTextEditor editor, ISpellcheckService? service, ISettingsService? settings)
    {
        _editor = editor;
        _service = service;
        _settings = settings;
    }

    internal bool DecorationsActive => _enabled && _editor.IsSpellcheckDecorationsEnabled && !_editor.IsReadOnly;

    internal IReadOnlyList<(Point From, Point To)>? UnderlineLines => _underlineLines;

    internal async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        if (_settings == null) return;

        _enabled = await _settings.GetAsync("Editor.SpellCheck", true).ConfigureAwait(false);
        var langs = await _settings.GetAsync("Editor.SpellCheckLanguages", "en").ConfigureAwait(false);
        _languages = ParseLanguageCodes(langs);
        _settings.SettingChanged += OnSettingChanged;
        Schedule(force: true);
    }

    internal void Detach()
    {
        Interlocked.Increment(ref _activeCheckId);
        _debounceTimer?.Stop();
        _debounceTimer = null;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (_settings != null)
            _settings.SettingChanged -= OnSettingChanged;
    }

    internal void Schedule(bool force = false)
    {
        if (!_editor.IsAttachedToVisualTree()) return;
        if (!DecorationsActive)
        {
            _cts?.Cancel();
            _debounceTimer?.Stop();
            ClearIssues();
            return;
        }

        if (force) _lastCheckedText = string.Empty;

        _debounceTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounceTimer.Tick -= OnDebounceTick;
        _debounceTimer.Tick += OnDebounceTick;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    internal void OnDecorationGateChanged()
    {
        _pen = null;
        _brush = null;
        if (!DecorationsActive)
        {
            _cts?.Cancel();
            _debounceTimer?.Stop();
            ClearIssues();
        }
        else
            Schedule(force: true);
    }

    internal void InvalidateGeometry() => _underlineLines = null;

    internal void RebuildGeometry(TextLayout? layout, int[]? boundaryMap, int layoutTextLength, int logicalTextLength)
    {
        if (layout == null || boundaryMap == null || !DecorationsActive)
        {
            _underlineLines = null;
            return;
        }

        List<SpellcheckIssue> issues;
        lock (_sync) issues = [.. _issues];
        if (issues.Count == 0) { _underlineLines = null; return; }

        var lines = new List<(Point, Point)>(issues.Count * 2);
        foreach (var issue in issues)
        {
            if (issue.Length <= 0) continue;
            int ls = Math.Clamp(issue.Start, 0, logicalTextLength);
            int le = Math.Clamp(issue.Start + issue.Length, 0, logicalTextLength);
            if (le <= ls) continue;

            var lStart = RichTextLayoutBuilder.LogicalCaretToLayoutBoundary(boundaryMap, ls);
            var lLen = RichTextLayoutBuilder.LogicalCaretToLayoutBoundary(boundaryMap, le) - lStart;
            if (lLen <= 0) continue;

            foreach (var rect in layout.HitTestTextRange(lStart, lLen))
            {
                if (rect.Width <= 0.5 || rect.Height <= 0.5) continue;
                var y = rect.Bottom - 1.0;
                lines.Add((new Point(rect.X, y), new Point(rect.Right, y)));
            }
        }

        _underlineLines = lines.Count > 0 ? lines : null;
    }

    internal IBrush GetUnderlineBrush()
    {
        if (_brush != null) return _brush;
        if (Application.Current?.TryFindResource("SystemFillColorCriticalBrush", out var resource) == true
            && resource is IBrush b)
            return _brush = b;
        return _brush = new SolidColorBrush(Color.FromRgb(0xE0, 0x45, 0x45));
    }

    internal Pen GetOrCreatePen() => _pen ??= new Pen(GetUnderlineBrush(), 1.2);

    internal bool TryGetIssueAtIndex(int index, out SpellcheckIssue issue)
    {
        lock (_sync)
        {
            issue = _issues.FirstOrDefault(i => index >= i.Start && index < i.Start + i.Length)
                ?? new SpellcheckIssue(0, 0, string.Empty, []);
            return issue.Length > 0;
        }
    }

    internal async Task SuggestAsync(SpellcheckIssue issue)
    {
        IReadOnlyList<string> suggestions = [];
        if (_service != null)
        {
            try
            {
                suggestions = await _service.SuggestAsync(issue.Word, _languages, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch { }
        }

        var items = new List<object>();
        foreach (var s in suggestions.Take(5))
        {
            var captured = s;
            var mi = new MenuItem { Header = captured, Cursor = new Cursor(StandardCursorType.Hand) };
            mi.Click += (_, _) =>
            {
                _editor.SelectionStart = issue.Start;
                _editor.SelectionEnd = issue.Start + issue.Length;
                _editor.CaretIndex = _editor.SelectionEnd;
                _editor.InsertTextAtCaret(captured);
                Schedule(force: true);
            };
            items.Add(mi);
        }

        if (items.Count == 0)
            items.Add(new MenuItem { Header = RichTextThemeBrushes.TNotes("NoSuggestions"), IsEnabled = false });

        items.Add(new Separator());
        var addItem = new MenuItem
        {
            Header = RichTextThemeBrushes.TNotes("AddWordToDictionary"),
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        addItem.Click += async (_, _) =>
        {
            if (_service == null) return;
            try
            {
                await _service.AddWordAsync(issue.Word, _languages, CancellationToken.None).ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => Schedule(force: true));
            }
            catch { }
        };
        items.Add(addItem);

        var menu = new ContextMenu();
        menu.ItemsSource = items;
        menu.Open(_editor);
    }

    private void ClearIssues()
    {
        Interlocked.Increment(ref _activeCheckId);
        lock (_sync)
        {
            _issues.Clear();
            _lastCheckedText = string.Empty;
        }
        _underlineLines = null;
        _editor.InvalidateVisual();
    }

    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounceTimer?.Stop();
        await RunAsync().ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        if (_service == null || !DecorationsActive) { ClearIssues(); return; }

        var checkId = Interlocked.Increment(ref _activeCheckId);
        var langs = _languages.Count == 0 ? ["en"] : _languages;
        var capturedSpans = _editor.Spans ?? Array.Empty<InlineSpan>();
        var capturedText = _editor.Text;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        try
        {
            var issues = await _service.CheckAsync(capturedSpans, langs, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (checkId != _activeCheckId) return;
                if (!string.Equals(_editor.Text, capturedText, StringComparison.Ordinal)) return;
                if (!DecorationsActive) { ClearIssues(); return; }

                lock (_sync)
                {
                    _issues.Clear();
                    _issues.AddRange(issues);
                    _lastCheckedText = capturedText;
                }
                _underlineLines = null;
                _editor.InvalidateVisual();
            });
        }
        catch (OperationCanceledException) { }
    }

    private async void OnSettingChanged(object? sender, string key)
    {
        if (!SettingKeys.Contains(key, StringComparer.Ordinal) || _settings == null) return;
        _enabled = await _settings.GetAsync("Editor.SpellCheck", true).ConfigureAwait(false);
        var langs = await _settings.GetAsync("Editor.SpellCheckLanguages", "en").ConfigureAwait(false);
        _languages = ParseLanguageCodes(langs);
        Schedule(force: true);
    }

    private static List<string> ParseLanguageCodes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ["en"];
        var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Replace('_', '-'))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return values.Count == 0 ? ["en"] : values;
    }
}
