namespace Mnemo.UI.Modules.Mindmap.ViewModels;

public partial class MindmapViewModel
{
    /// <summary>Effective minimap mode: local override for this mindmap, or global default from settings.</summary>
    public string MinimapVisibilityMode
    {
        get => _localMinimapOverride ?? _globalMinimapDefault;
        set
        {
            if (string.IsNullOrEmpty(value)) return;
            _localMinimapOverride = value;
            NotifyMinimapVisibilityChanged();
            if (_session.Current != null && _settingsService != null)
                _ = SaveMinimapOverrideAsync(_session.Current.Id, value);
        }
    }

    public bool IsMinimapOff { get => MinimapVisibilityMode == "Off"; set { if (value) MinimapVisibilityMode = "Off"; } }
    public bool IsMinimapAuto { get => MinimapVisibilityMode == "Auto"; set { if (value) MinimapVisibilityMode = "Auto"; } }
    public bool IsMinimapOn { get => MinimapVisibilityMode == "On"; set { if (value) MinimapVisibilityMode = "On"; } }

    private void SetMinimapVisibility(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return;
        MinimapVisibilityMode = mode;
    }

    private async Task SaveMinimapOverrideAsync(string mindmapId, string mode)
    {
        if (_settingsService == null) return;
        var overrides = await _settingsService.GetAsync(MinimapOverridesKey, new Dictionary<string, string>()).ConfigureAwait(false)
            ?? new Dictionary<string, string>();
        if (mode == _globalMinimapDefault)
            overrides.Remove(mindmapId);
        else
            overrides[mindmapId] = mode;
        await _settingsService.SetAsync(MinimapOverridesKey, overrides).ConfigureAwait(false);
    }

    public async Task RefreshGlobalMinimapSettingAsync()
    {
        if (_settingsService == null) return;
        var mode = await _settingsService.GetAsync("Mindmap.MinimapVisibility", "Auto").ConfigureAwait(false);
        if (mode == null) return;
        _globalMinimapDefault = mode;
        var overrides = await _settingsService.GetAsync(MinimapOverridesKey, new Dictionary<string, string>()).ConfigureAwait(false)
            ?? new Dictionary<string, string>();
        if (overrides.Count > 0)
        {
            overrides.Clear();
            await _settingsService.SetAsync(MinimapOverridesKey, overrides).ConfigureAwait(false);
        }

        _localMinimapOverride = null;
        NotifyMinimapVisibilityChanged();
    }

    private void NotifyMinimapVisibilityChanged()
    {
        OnPropertyChanged(nameof(MinimapVisibilityMode));
        OnPropertyChanged(nameof(IsMinimapOff));
        OnPropertyChanged(nameof(IsMinimapAuto));
        OnPropertyChanged(nameof(IsMinimapOn));
    }
}
