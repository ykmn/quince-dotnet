using Quince.Service.Configuration;

namespace Quince.Service.Services;

/// <summary>
/// Scoped (per-circuit) UI state so the burger menu (in MainLayout) and channel cards (nested inside
/// Index's @Body) can open the same dialogs without threading callbacks through every layer.
/// </summary>
public class ChannelUiState
{
    public event Action? Changed;

    public bool EditDialogOpen { get; private set; }
    public ChannelConfig? EditTarget { get; private set; } // null while EditDialogOpen => create mode

    public ChannelConfig? DeleteTarget { get; private set; }
    public ChannelConfig? CloneTarget { get; private set; }

    public bool IndicatorsOpen { get; private set; }
    public string? IndicatorsChannelName { get; private set; }

    public bool SettingsOpen { get; private set; }
    public bool ReadmeOpen { get; private set; }

    /// <summary>Whether channel cards show a selection checkbox and the toolbar shows "Изменить…"
    /// instead of the status message — the "Массовое изменение" (bulk edit) menu action's mode.</summary>
    public bool BulkSelectMode { get; private set; }

    /// <summary>Channel <see cref="ChannelConfig.Filename"/>s currently checked while
    /// <see cref="BulkSelectMode"/> is on.</summary>
    public HashSet<string> SelectedChannelFilenames { get; } = new();

    public bool BulkEditOpen { get; private set; }

    /// <summary>The admin-only "Монитор ресурсов" dialog (burger menu) — gated at the menu-item
    /// level by <see cref="Auth.CurrentUserContext.CanManage"/>, not here, same as every other
    /// management dialog this class opens.</summary>
    public bool ResourceMonitorOpen { get; private set; }

    public void ToggleBulkSelectMode()
    {
        if (BulkSelectMode) EndBulkSelect();
        else
        {
            BulkSelectMode = true;
            Notify();
        }
    }

    /// <summary>Turns bulk-select off, clears the checked set, and closes the bulk-edit dialog if it
    /// was open — used both when the menu action is toggled off and after a successful apply.</summary>
    public void EndBulkSelect()
    {
        BulkSelectMode = false;
        SelectedChannelFilenames.Clear();
        BulkEditOpen = false;
        Notify();
    }

    public void ToggleChannelSelected(string filename)
    {
        if (!SelectedChannelFilenames.Remove(filename))
            SelectedChannelFilenames.Add(filename);
        Notify();
    }

    public void SelectAll(IEnumerable<string> filenames)
    {
        SelectedChannelFilenames.Clear();
        foreach (var filename in filenames) SelectedChannelFilenames.Add(filename);
        Notify();
    }

    public void DeselectAll()
    {
        SelectedChannelFilenames.Clear();
        Notify();
    }

    public void OpenBulkEdit()
    {
        if (SelectedChannelFilenames.Count == 0) return;
        BulkEditOpen = true;
        Notify();
    }

    public void CloseBulkEdit()
    {
        BulkEditOpen = false;
        Notify();
    }

    /// <summary>Whether the source URL / save-path text on every channel card is masked (behind
    /// `***`) — a single flag shared by all cards (not per-card, not per-field) so clicking any one
    /// card's URL or path hides/reveals it everywhere at once, e.g. right before screen-sharing.</summary>
    public bool ValuesMasked { get; private set; }

    public void ToggleValuesMasked()
    {
        ValuesMasked = !ValuesMasked;
        Notify();
    }

    /// <summary>Shown under the filter field on the channel list — result of the last
    /// refresh-config/restart/start-all/stop-all menu action.</summary>
    public string? StatusMessage { get; private set; }

    public void SetStatusMessage(string message)
    {
        StatusMessage = message;
        Notify();
    }

    public void ClearStatusMessage()
    {
        StatusMessage = null;
        Notify();
    }

    public void OpenCreateChannel()
    {
        EditTarget = null;
        EditDialogOpen = true;
        Notify();
    }

    public void OpenEditChannel(ChannelConfig config)
    {
        EditTarget = config;
        EditDialogOpen = true;
        Notify();
    }

    public void CloseEditChannel()
    {
        EditDialogOpen = false;
        EditTarget = null;
        Notify();
    }

    public void OpenDeleteChannel(ChannelConfig config)
    {
        DeleteTarget = config;
        Notify();
    }

    public void CloseDeleteChannel()
    {
        DeleteTarget = null;
        Notify();
    }

    public void OpenCloneChannel(ChannelConfig config)
    {
        CloneTarget = config;
        Notify();
    }

    public void CloseCloneChannel()
    {
        CloneTarget = null;
        Notify();
    }

    public void OpenIndicators(string channelName)
    {
        IndicatorsChannelName = channelName;
        IndicatorsOpen = true;
        Notify();
    }

    public void CloseIndicators()
    {
        IndicatorsOpen = false;
        IndicatorsChannelName = null;
        Notify();
    }

    public void OpenSettings()
    {
        SettingsOpen = true;
        Notify();
    }

    public void CloseSettings()
    {
        SettingsOpen = false;
        Notify();
    }

    public void OpenReadme()
    {
        ReadmeOpen = true;
        Notify();
    }

    public void CloseReadme()
    {
        ReadmeOpen = false;
        Notify();
    }

    public void OpenResourceMonitor()
    {
        ResourceMonitorOpen = true;
        Notify();
    }

    public void CloseResourceMonitor()
    {
        ResourceMonitorOpen = false;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}
