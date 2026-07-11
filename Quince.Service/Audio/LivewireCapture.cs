using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;
using Quince.Service.Services;

namespace Quince.Service.Audio;

/// <summary>
/// Captures a Livewire AoIP channel (<c>source.type: livewire</c>) by handing ffmpeg an SDP file
/// describing the channel's multicast RTP stream, exactly the recipe confirmed to work by
/// R. Porterfield's "Receiving Livewire/AES67 in ffmpeg" writeup: an SDP with
/// <c>c=IN IP4 &lt;multicast address&gt;</c> / <c>m=audio 5004 RTP/AVP 96</c> /
/// <c>a=rtpmap:96 L24/48000/2</c>, fed to ffmpeg via
/// <c>-protocol_whitelist file,udp,rtp -localaddr &lt;NIC IPv4&gt; -i channel.sdp</c>. This is the
/// same "spawn ffmpeg, read raw f32le off stdout" shape as <see cref="StreamCapture"/>, so it
/// shares all process/reconnect/stall-watchdog plumbing via <see cref="FfmpegPipedCapture"/> —
/// only <see cref="BuildArgs"/> differs (an SDP file instead of a URL).
///
/// The multicast address is never stored in config — it's always re-derived from
/// <see cref="SourceConfig.LivewireChannelNumber"/> via <see cref="LivewireAddressing"/>, so it can
/// never drift out of sync with the channel number the user actually picked.
///
/// This backend has NOT been verified against a real Livewire network yet (no AoIP hardware on the
/// development machine) — verification is expected to happen on a separate machine that has one.
/// Every step that could plausibly go wrong on a real network (NIC resolution, multicast join,
/// ffmpeg's own stderr) is logged deliberately verbosely for that reason — see the log lines below
/// before assuming a silent failure.
/// </summary>
public sealed class LivewireCapture : FfmpegPipedCapture
{
    public const int SampleRate = 48000;

    private readonly SourceConfig _source;
    private readonly string _nic;
    private readonly int _channels;
    private readonly string _sdpPath;

    public LivewireCapture(string ffmpegPath, SourceConfig source, string nic, Func<int> getReconnectDelaySeconds,
        Func<int> getMaxReconnectAttempts, ILogger log, Action? onReconnectExhausted = null, string channelName = "")
        : base(ffmpegPath, getReconnectDelaySeconds, getMaxReconnectAttempts, log, onReconnectExhausted, channelName)
    {
        _source = source;
        _nic = nic;
        _channels = source.LivewireStereo ? 2 : 1;
        // One temp SDP file per instance (not per connection attempt) — its content depends only on
        // config that doesn't change within this instance's lifetime (ChannelEngine.PipelineChanged
        // already restarts the whole engine, and therefore this capture, on any Livewire* field
        // change), so re-deriving the path each attempt would just be pointless GUID churn.
        _sdpPath = Path.Combine(Path.GetTempPath(), $"quince-livewire-{Guid.NewGuid():N}.sdp");
    }

    protected override int GetSampleRate() => SampleRate;
    protected override int GetChannels() => _channels;

    protected override string TargetDescription =>
        $"Livewire-каналу {_source.LivewireChannelNumber} ({_source.LivewireChannelName}) через {_nic}";

    protected override string[] BuildArgs()
    {
        if (!LivewireAddressing.IsValidChannelNumber(_source.LivewireChannelNumber))
            throw new InvalidOperationException(
                $"Некорректный номер канала Livewire: {_source.LivewireChannelNumber} (допустимо {LivewireAddressing.MinChannelNumber}..{LivewireAddressing.MaxChannelNumber})");

        if (string.IsNullOrEmpty(_nic))
            throw new InvalidOperationException(
                "Сетевой интерфейс Livewire не выбран — укажите его в «Настройках приложения» (общий для всех Livewire-каналов)");

        var nicIp = NetworkInterfaceEnumerator.ResolveNicIPv4(_nic);
        if (nicIp == null)
            throw new InvalidOperationException(
                $"Сетевой интерфейс '{_nic}' для Livewire не найден или не имеет IPv4-адреса — проверьте «Настройки приложения» и что адаптер включён");

        var multicastAddress = LivewireAddressing.ChannelToMulticastAddress(_source.LivewireChannelNumber);

        var sdp = string.Join("\r\n", new[]
        {
            "v=0",
            $"o=- {_source.LivewireChannelNumber} 1 IN IP4 0.0.0.0",
            "s=Quince Livewire capture",
            $"c=IN IP4 {multicastAddress}",
            "t=0 0",
            "a=type:multicast",
            $"m=audio {LivewireAddressing.AudioPort} RTP/AVP 96",
            $"a=rtpmap:96 L24/{SampleRate}/{_channels}",
            "",
        });
        File.WriteAllText(_sdpPath, sdp);

        Log.LogInformation(
            "Livewire: канал {Number} ({Name}), мультикаст {Address}:{Port}, интерфейс {Nic} ({NicIp}), {Channels} канал(ов), SDP: {SdpPath}",
            _source.LivewireChannelNumber, _source.LivewireChannelName, multicastAddress, LivewireAddressing.AudioPort,
            _nic, nicIp, _channels, _sdpPath);
        Log.LogDebug("Livewire: содержимое SDP-файла:\r\n{Sdp}", sdp);

        return new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-protocol_whitelist", "file,udp,rtp",
            "-localaddr", nicIp,
            "-i", _sdpPath,
            "-vn",
            "-acodec", "pcm_f32le",
            "-ar", SampleRate.ToString(),
            "-ac", _channels.ToString(),
            "-f", "f32le",
            "pipe:1",
        };
    }

    protected override void OnStopped()
    {
        try { if (File.Exists(_sdpPath)) File.Delete(_sdpPath); }
        catch (Exception ex) { Log.LogDebug(ex, "Не удалось удалить временный SDP-файл {SdpPath}", _sdpPath); }
    }
}
