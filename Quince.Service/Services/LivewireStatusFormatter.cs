using Quince.Service.Audio.Livewire;

namespace Quince.Service.Services;

/// <summary>Renders a <see cref="LivewireDiscoveryStatus"/> snapshot into UI text/CSS class — shared
/// between <c>ChannelEditDialog</c> (the Livewire tab's status line) and <c>AppSettingsDialog</c> (next
/// to the Подключить/Отключить toggle), so the two don't drift into describing the same states
/// differently.</summary>
public static class LivewireStatusFormatter
{
    public static string Text(LivewireDiscoveryStatus? status, LocalizationService loc) => status?.State switch
    {
        null or LivewireDiscoveryState.Disabled => loc["channelEdit.livewireStatusDisabled"],
        LivewireDiscoveryState.Disconnected => loc["channelEdit.livewireStatusDisconnected"],
        LivewireDiscoveryState.NicNotFound => loc["channelEdit.livewireStatusNicNotFound"],
        LivewireDiscoveryState.SocketError => loc.T("channelEdit.livewireStatusSocketError", status.ErrorMessage ?? ""),
        LivewireDiscoveryState.Listening => status.LastPacketAt is DateTimeOffset lastPacket
            ? loc.T("channelEdit.livewireStatusListeningWithTraffic", status.NicIp ?? "",
                ChannelDisplayFormatter.FormatDuration((int)Math.Max(0, (DateTimeOffset.UtcNow - lastPacket).TotalSeconds), loc))
            : loc.T("channelEdit.livewireStatusListeningNoTraffic", status.NicIp ?? ""),
        _ => "",
    };

    public static string CssClass(LivewireDiscoveryStatus? status) => status?.State switch
    {
        LivewireDiscoveryState.Listening => "form-hint-ok",
        LivewireDiscoveryState.Disconnected or LivewireDiscoveryState.NicNotFound or LivewireDiscoveryState.SocketError => "form-hint-warn",
        _ => "form-hint-neutral",
    };
}
