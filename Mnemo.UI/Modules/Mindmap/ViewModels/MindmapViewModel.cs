using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mnemo.Core.Enums;
using Mnemo.Core.Models.Mindmap;
using Mnemo.Core.Services;
using LayoutAlgorithms = Mnemo.Core.Models.Mindmap.LayoutAlgorithms;
using Mnemo.UI.Modules.Mindmap.Services;
using Mnemo.UI.ViewModels;
using MindmapModel = Mnemo.Core.Models.Mindmap.Mindmap;

namespace Mnemo.UI.Modules.Mindmap.ViewModels;

public partial class MindmapViewModel : ViewModelBase, INavigationAware
{
    private const string MinimapOverridesKey = "Mindmap.MinimapVisibilityOverrides";
    private const string MinimapShowCollapsedKey = "Mindmap.MinimapShowCollapsedNodes";

    private readonly IMindmapService _mindmapService;
    private readonly MindmapEditorSession _session;
    private readonly MindmapEditorHistory _history;
    private readonly MindmapGraphMutator _mutator;
    private readonly MindmapEdgeHoverState _hover;
    private readonly ISettingsService? _settingsService;
    private readonly IOverlayService? _overlayService;
    private readonly ILocalizationService? _localizationService;
    private readonly ILoggerService? _logger;

    private string _globalMinimapDefault = "Auto";
    private string? _localMinimapOverride;

    [ObservableProperty] private string _title = "Mindmap";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _toolbarCategory = "Edit";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditMode))]
    [NotifyPropertyChangedFor(nameof(IsPreviewMode))]
    [NotifyPropertyChangedFor(nameof(IsToolbarVisible))]
    [NotifyPropertyChangedFor(nameof(IsEditingEnabled))]
    private string _mindmapMode = "Edit";
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private bool _showEdgeLabels = true;
    [ObservableProperty] private bool _showCollapsedNodesOnMinimap;
    [ObservableProperty] private bool _exportPngTransparentBackground;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLayoutTreeVertical))]
    [NotifyPropertyChangedFor(nameof(IsLayoutTreeHorizontal))]
    [NotifyPropertyChangedFor(nameof(IsLayoutRadial))]
    private string _selectedLayoutAlgorithm = LayoutAlgorithms.TreeVertical;

    public bool IsEditMode => MindmapMode == "Edit";
    public bool IsPreviewMode => MindmapMode == "Preview";
    public bool IsToolbarVisible => !IsPreviewMode;
    public bool IsEditingEnabled => !IsPreviewMode;
    public bool CanUndo => _history.CanUndo;
    public bool CanRedo => _history.CanRedo;
    public bool SuppressRecenterOnNextCollectionChange { get; set; }

    public ObservableCollection<NodeViewModel> Nodes => _session.Nodes;
    public ObservableCollection<EdgeViewModel> Edges => _session.Edges;
    public MindmapCanvasSettings CanvasSettings { get; } = new();

    public ICommand AddNodeCommand { get; }
    public ICommand DeleteSelectedCommand { get; }
    public ICommand ConnectSelectedCommand { get; }
    public ICommand DetachSelectedCommand { get; }
    public ICommand SetLayoutAlgorithmCommand { get; }
    public ICommand RecenterCommand { get; }
    public ICommand SetSelectedNodesColorCommand { get; }
    public ICommand SetSelectedNodesShapeCommand { get; }
    public ICommand SetSelectedEdgeKindCommand { get; }
    public ICommand SetSelectedEdgeTypeCommand { get; }
    public ICommand SetMinimapVisibilityCommand { get; }
    public ICommand SetToolbarCategoryCommand { get; }
    public ICommand SetMindmapModeCommand { get; }
    public ICommand ExportAsPngCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand RedoCommand { get; }
    public ICommand ToggleCollapseCommand { get; }

    public event EventHandler? RecenterRequested;
    public event Action<EdgeViewModel>? FocusEdgeLabelRequested;
    public event EventHandler? ExportRequested;

    public MindmapViewModel(
        IMindmapService mindmapService,
        MindmapEditorSession session,
        MindmapEditorHistory history,
        MindmapGraphMutator mutator,
        MindmapEdgeHoverState hover,
        ISettingsService? settingsService = null,
        IOverlayService? overlayService = null,
        ILocalizationService? localizationService = null,
        ILoggerService? logger = null)
    {
        _mindmapService = mindmapService;
        _session = session;
        _history = history;
        _mutator = mutator;
        _hover = hover;
        _settingsService = settingsService;
        _overlayService = overlayService;
        _localizationService = localizationService;
        _logger = logger;

        _session.SetNodePropertyHandler(OnNodePropertyChanged);
        _history.ConfigureRestore(RestoreMindmapStateAsync);
        _history.StateChanged += OnHistoryStateChanged;

        if (_settingsService != null)
        {
            _settingsService.SettingChanged += OnSettingsChanged;
            _ = RefreshCanvasSettingsAsync();
        }

        AddNodeCommand = new AsyncRelayCommand(() => AddNodeAsync());
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync);
        ConnectSelectedCommand = new AsyncRelayCommand(ConnectSelectedAsync);
        DetachSelectedCommand = new AsyncRelayCommand(DetachSelectedAsync);
        SetLayoutAlgorithmCommand = new AsyncRelayCommand<string?>(SetLayoutAlgorithmAsync);
        RecenterCommand = new RelayCommand(() => RecenterRequested?.Invoke(this, EventArgs.Empty));
        SetSelectedNodesColorCommand = new RelayCommand<string?>(SetSelectedNodesColor);
        SetSelectedNodesShapeCommand = new RelayCommand<string?>(SetSelectedNodesShape);
        SetSelectedEdgeKindCommand = new RelayCommand<MindmapEdgeKind?>(SetSelectedEdgeKind);
        SetSelectedEdgeTypeCommand = new RelayCommand<string?>(SetSelectedEdgeType);
        SetMinimapVisibilityCommand = new RelayCommand<string?>(SetMinimapVisibility);
        SetToolbarCategoryCommand = new RelayCommand<string?>(c => { if (!string.IsNullOrEmpty(c)) ToolbarCategory = c; });
        SetMindmapModeCommand = new RelayCommand<string?>(c => { if (!string.IsNullOrEmpty(c)) MindmapMode = c; });
        ExportAsPngCommand = new RelayCommand(() => ExportRequested?.Invoke(this, EventArgs.Empty));
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo);
        RedoCommand = new AsyncRelayCommand(RedoAsync, () => CanRedo);
        ToggleCollapseCommand = new AsyncRelayCommand<NodeViewModel?>(ToggleCollapseAsync);
    }

    private MindmapEditorDefaults EditorDefaults => new(
        DefaultNodeColor, DefaultNodeShape, DefaultEdgeKind, DefaultEdgeType);

    private void OnHistoryStateChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        (UndoCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RedoCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private async Task RestoreMindmapStateAsync(MindmapModel m)
    {
        SelectedLayoutAlgorithm = _session.Refresh(m);
        Title = m.Title;
        SuppressRecenterOnNextCollectionChange = true;
        await _mindmapService.SaveMindmapAsync(m);
    }

    public async Task UndoAsync()
    {
        if (!_history.CanUndo) return;
        await _history.UndoAsync();
    }

    public async Task RedoAsync()
    {
        if (!_history.CanRedo) return;
        await _history.RedoAsync();
    }

    public string Translate(string key, string ns, string fallback) =>
        _localizationService?.T(key, ns) ?? fallback;

    public async Task ShowExportErrorAsync(string message)
    {
        if (_overlayService == null) return;
        var title = Translate("ExportFailedTitle", "Mindmap", "Export failed");
        await _overlayService.CreateDialogAsync(title, message).ConfigureAwait(true);
    }

    public void LogExportWarning(Exception ex) =>
        _logger?.Log(LogLevel.Warning, nameof(MindmapViewModel), "PNG export failed", ex);

    partial void OnShowCollapsedNodesOnMinimapChanged(bool value)
    {
        if (_settingsService == null) return;
        _ = _settingsService.SetAsync(MinimapShowCollapsedKey, value);
    }

    partial void OnToolbarCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsEditTab));
        OnPropertyChanged(nameof(IsStyleTab));
        OnPropertyChanged(nameof(IsViewTab));
    }

    partial void OnSelectedLayoutAlgorithmChanged(string value)
    {
        if (_session.Current == null || string.IsNullOrEmpty(value)) return;
        if (_session.Current.Layout.Algorithm == value) return;
        _session.Current.Layout.Algorithm = value;
        _ = _mindmapService.UpdateLayoutAlgorithmAsync(_session.Current.Id, value);
    }

    private void OnSettingsChanged(object? sender, string key)
    {
        if (key.StartsWith("Mindmap.Grid", StringComparison.Ordinal) || key == "Mindmap.ModifierBehaviour")
            _ = RefreshCanvasSettingsAsync();
        else if (key == "Mindmap.MinimapVisibility")
            _ = RefreshGlobalMinimapSettingAsync();
        else if (key == "Mindmap.MinimapShowCollapsedNodes")
            _ = RefreshGlobalMinimapShowCollapsedNodesSettingAsync();
    }

    public async Task RefreshCanvasSettingsAsync()
    {
        if (_settingsService == null) return;

        CanvasSettings.GridType = await _settingsService.GetAsync("Mindmap.GridType", "Dotted").ConfigureAwait(false);
        CanvasSettings.ModifierBehaviour = await _settingsService.GetAsync("Mindmap.ModifierBehaviour", "Selecting").ConfigureAwait(false);

        var sizeStr = await _settingsService.GetAsync("Mindmap.GridSize", "40").ConfigureAwait(false);
        var dotSizeStr = await _settingsService.GetAsync("Mindmap.GridDotSize", "1.5").ConfigureAwait(false);
        var opacityStr = await _settingsService.GetAsync("Mindmap.GridOpacity", "0.2").ConfigureAwait(false);

        if (double.TryParse(sizeStr, System.Globalization.CultureInfo.InvariantCulture, out var size))
            CanvasSettings.GridSpacing = size;
        if (double.TryParse(dotSizeStr, System.Globalization.CultureInfo.InvariantCulture, out var dotSize))
            CanvasSettings.GridDotSize = dotSize;
        if (double.TryParse(opacityStr, System.Globalization.CultureInfo.InvariantCulture, out var opacity))
            CanvasSettings.GridOpacity = opacity;
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (_settingsService != null)
        {
            _ = LoadMinimapSettingAsync();
            _ = RefreshCanvasSettingsAsync();
        }

        if (parameter is string id)
            _ = LoadMindmapAsync(id);
        else
            _ = LoadInitialMindmapAsync();
    }

    private async Task LoadMinimapSettingAsync()
    {
        if (_settingsService == null) return;
        var mode = await _settingsService.GetAsync("Mindmap.MinimapVisibility", "Auto").ConfigureAwait(false);
        if (mode != null) _globalMinimapDefault = mode;
        ShowCollapsedNodesOnMinimap = await _settingsService.GetAsync(MinimapShowCollapsedKey, false).ConfigureAwait(false);
        if (_session.Current == null && _localMinimapOverride == null)
            NotifyMinimapVisibilityChanged();
    }

    public async Task RefreshGlobalMinimapShowCollapsedNodesSettingAsync()
    {
        if (_settingsService == null) return;
        ShowCollapsedNodesOnMinimap = await _settingsService.GetAsync(MinimapShowCollapsedKey, false).ConfigureAwait(false);
    }

    private async Task LoadInitialMindmapAsync()
    {
        IsLoading = true;
        try
        {
            var mindmapsResult = await _mindmapService.GetAllMindmapsAsync();
            if (mindmapsResult.IsSuccess && mindmapsResult.Value != null && mindmapsResult.Value.Any())
                await LoadMindmapAsync(mindmapsResult.Value.First().Id);
            else
            {
                var createResult = await _mindmapService.CreateMindmapAsync("My First Mindmap");
                if (createResult.IsSuccess && createResult.Value != null)
                    await LoadMindmapAsync(createResult.Value.Id);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadMindmapAsync(string id)
    {
        var result = await _mindmapService.GetMindmapAsync(id);
        if (!result.IsSuccess || result.Value == null) return;

        bool isNewMindmap = _session.Current?.Id != id;
        if (isNewMindmap)
            _history.Clear();

        SelectedLayoutAlgorithm = _session.Refresh(result.Value);
        Title = result.Value.Title;

        if (_settingsService != null)
        {
            var overrides = await _settingsService.GetAsync(MinimapOverridesKey, new Dictionary<string, string>()).ConfigureAwait(false)
                ?? new Dictionary<string, string>();
            _localMinimapOverride = overrides.TryGetValue(id, out var saved) ? saved : null;
        }
        else
            _localMinimapOverride = null;

        NotifyMinimapVisibilityChanged();
    }

    private async Task AddNodeAsync()
    {
        try
        {
            await _mutator.AddNodeAsync(EditorDefaults, Nodes.Where(n => n.IsSelected).ToList());
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to add node", ex);
        }
    }

    public async Task DeleteSelectedAsync() =>
        await _mutator.DeleteSelectedAsync(Nodes.Where(n => n.IsSelected).ToList());

    private async Task ConnectSelectedAsync() =>
        await _mutator.ConnectSelectedAsync(EditorDefaults, Nodes.Where(n => n.IsSelected).ToList());

    private async Task DetachSelectedAsync() =>
        await _mutator.DetachSelectedAsync(Nodes.Where(n => n.IsSelected).ToList());

    public void CopySelection()
    {
        try
        {
            _mutator.CopySelection(Nodes.Where(n => n.IsSelected).ToList());
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to copy selection", ex);
        }
    }

    public async Task PasteAsync()
    {
        try
        {
            var newIds = await _mutator.PasteAsync(EditorDefaults, FirstSelectedNode, Nodes.FirstOrDefault());
            if (newIds.Count > 0)
            {
                foreach (var node in Nodes)
                    node.IsSelected = newIds.Contains(node.Id);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to paste nodes", ex);
        }
    }

    public async Task DuplicateSelectionAsync()
    {
        try
        {
            await _mutator.DuplicateSelectionAsync(EditorDefaults, Nodes.Where(n => n.IsSelected).ToList());
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to duplicate nodes", ex);
        }
    }

    public async Task AddChildNodeAsync()
    {
        try
        {
            if (FirstSelectedNode == null) return;
            await _mutator.AddChildNodeAsync(EditorDefaults, FirstSelectedNode);
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to add child node", ex);
        }
    }

    public async Task AddSiblingNodeAsync()
    {
        try
        {
            if (FirstSelectedNode == null) return;
            await _mutator.AddSiblingNodeAsync(EditorDefaults, FirstSelectedNode);
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to add sibling node", ex);
        }
    }

    public async Task UpdateNodeTextAsync(NodeViewModel node, string text) =>
        await _mutator.UpdateNodeTextAsync(node, text);

    public MindmapModel? CaptureMoveSnapshot() => _mutator.CaptureMoveSnapshot();

    public async Task UpdateNodesPositionAsync(MindmapModel before, IReadOnlyList<(NodeViewModel node, double x, double y)> moves) =>
        await _mutator.UpdateNodesPositionAsync(before, moves);

    public async Task UpdateNodePositionAsync(NodeViewModel node, double x, double y)
    {
        if (_session.Current == null) return;
        var before = _history.Snapshot(_session.Current);
        await _mutator.UpdateNodesPositionAsync(before, new[] { (node, x, y) });
    }

    private async Task SetLayoutAlgorithmAsync(string? algorithmId)
    {
        if (_session.Current == null || string.IsNullOrEmpty(algorithmId) || !LayoutAlgorithmIds.Contains(algorithmId))
            return;
        SelectedLayoutAlgorithm = algorithmId;
        await _mutator.ApplyLayoutAsync();
    }

    private async Task ToggleCollapseAsync(NodeViewModel? node)
    {
        try
        {
            if (node == null) return;
            await _mutator.ToggleCollapseAsync(node);
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to toggle node collapse", ex);
        }
    }

    public async void EdgeClicked(EdgeViewModel edge)
    {
        try
        {
            if (_session.Current == null) return;
            SelectedEdge = edge;
            if (edge.Label == null)
            {
                ShowEdgeLabels = true;
                await _mutator.AddEdgeLabelAsync(edge);
            }
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to process edge click", ex);
        }
    }

    public void BeginEditSelectedEdgeLabel()
    {
        if (SelectedEdge == null || !IsEditingEnabled) return;
        EdgeClicked(SelectedEdge);
        FocusEdgeLabelRequested?.Invoke(SelectedEdge);
    }

    public async void CommitEdgeLabel(EdgeViewModel edge)
    {
        try
        {
            await _mutator.CommitEdgeLabelAsync(edge);
        }
        catch (Exception ex)
        {
            _logger?.Error(nameof(MindmapViewModel), "Failed to commit edge label", ex);
        }
    }

    public void SetHoveredEdge(string? edgeId) => _hover.SetHoveredEdge(edgeId);
    public void SetHoveredNode(string nodeId, bool hovered) => _hover.SetHoveredNode(nodeId, hovered);
    public void ClearHoverState() => _hover.Clear();

    private void OnNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NodeViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(FirstSelectedNode));
            OnPropertyChanged(nameof(HasSelectedNodes));
            NotifyEffectiveStyleChanged();
        }
        else if (e.PropertyName is nameof(NodeViewModel.Color) or nameof(NodeViewModel.Shape))
        {
            NotifyEffectiveStyleChanged();
        }
    }
}
