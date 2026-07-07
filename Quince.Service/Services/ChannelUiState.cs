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

    private void Notify() => Changed?.Invoke();
}
