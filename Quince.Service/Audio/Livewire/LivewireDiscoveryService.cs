using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quince.Service.Configuration;
using Quince.Service.Services;

namespace Quince.Service.Audio.Livewire;

public enum LivewireDiscoveryState
{
    /// <summary>No NIC selected in App Settings — nothing to connect to.</summary>
    Disabled,
    /// <summary>A NIC is selected but the socket isn't open right now — either the operator disconnected
    /// it via the "Подключить/Отключить" toggle in App Settings, or the app just started and hasn't
    /// connected yet. Reachable only via <see cref="LivewireDiscoveryService.DisconnectAsync"/>, never
    /// set automatically by a failure (those get their own, more specific states below).</summary>
    Disconnected,
    /// <summary>A NIC is selected but couldn't be resolved to a live IPv4 address (unplugged, renamed, etc.).</summary>
    NicNotFound,
    /// <summary>NIC resolved fine, but opening/binding the UDP socket itself failed (e.g. port already in use).</summary>
    SocketError,
    /// <summary>Socket is open and the receive loop is running — doesn't by itself mean packets have arrived yet, see <see cref="LivewireDiscoveryStatus.LastPacketAt"/>.</summary>
    Listening,
}

/// <summary>Point-in-time snapshot of what the discovery service is currently doing, for the
/// <c>ChannelEditDialog</c> Livewire tab to show the operator — distinct from <see cref="DiscoveredLivewireChannel"/>,
/// which is about the channels found, not the discovery process's own health.</summary>
public sealed record LivewireDiscoveryStatus(LivewireDiscoveryState State, string? NicIp, string? ErrorMessage, DateTimeOffset? LastPacketAt);

/// <summary>
/// App-wide (not per-channel) listener for the Livewire "Advertisement" multicast group
/// (239.192.255.3:4001), which real Axia nodes/consoles/routers use to auto-discover source channel
/// numbers/names on the AoIP network. One instance serves every <c>ChannelEditDialog</c>'s Livewire
/// tab, since Advertisement traffic describes the whole network's sources, not any one channel
/// being edited.
///
/// The packet format was reverse-engineered from live captures — see <see cref="LivewireAdvertisementParser"/>
/// and <c>LIVEWIRE.md</c> at the repo root for the byte-level writeup. The port isn't a publicly
/// documented constant (unlike audio's 5004) — 4001 is what was observed on the network this was
/// reverse-engineered on and is fixed here rather than made configurable, per the user's call.
///
/// Advertisement alone doesn't name every channel (some devices only send a number — see
/// <see cref="LivewireAdvertisementParser"/>'s doc comment), so this service also opportunistically
/// queries each newly-seen node's LWRP server (<see cref="LwrpClient"/>) once for its "SRC" table and
/// uses that to fill in names Advertisement didn't provide — never to overwrite a name Advertisement
/// already gave, since that came straight from the device the channel actually belongs to.
///
/// A device doesn't necessarily repeat every field on every Advertisement burst it sends (a "lite"
/// packet with no <c>ATRN</c>/<c>PSNM</c> can arrive for the same channel that a fuller packet named
/// moments earlier — see LIVEWIRE.md) — <see cref="HandlePacket"/> therefore merges each new sighting
/// into whatever's already known rather than replacing it outright, so a later empty field can't wipe
/// out a name a previous packet already established.
///
/// The socket doesn't have to live for the whole process lifetime: <see cref="ConnectAsync"/>/
/// <see cref="DisconnectAsync"/> let the App Settings dialog open/close it on demand (a
/// "Подключить"/"Отключить" toggle next to the NIC picker), so switching the Livewire network
/// interface no longer needs an app restart — only <see cref="StartAsync"/>'s own initial connect
/// (based on whatever NIC was already saved) still happens once, at hosted-service startup.
/// </summary>
public sealed class LivewireDiscoveryService : IHostedService
{
    public const string AdvertisementMulticastAddress = "239.192.255.3";
    public const int AdvertisementPort = 4001;
    private static readonly TimeSpan LwrpTimeout = TimeSpan.FromSeconds(3);

    private readonly AppSettingsService _appSettings;
    private readonly YamlConfigLoader _configLoader;
    private readonly string _configDir;
    private readonly ILogger<LivewireDiscoveryService> _logger;

    private readonly ConcurrentDictionary<int, DiscoveredLivewireChannel> _channels = new();
    private readonly ConcurrentDictionary<string, byte> _lwrpQueriedNodes = new();

    private volatile LivewireDiscoveryStatus _status = new(LivewireDiscoveryState.Disabled, null, null, null);

    // Guards Connect/Disconnect (and the startup connect and shutdown) against overlapping each
    // other — e.g. a UI click landing while StopAsync is already tearing the socket down.
    private readonly SemaphoreSlim _connectLock = new(1, 1);

    private UdpClient? _client;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;

    public LivewireDiscoveryService(AppSettingsService appSettings, YamlConfigLoader configLoader,
        IConfiguration configuration, ILogger<LivewireDiscoveryService> logger)
    {
        _appSettings = appSettings;
        _configLoader = configLoader;
        _configDir = PathResolver.Resolve(configuration["ConfigDir"], "config");
        _logger = logger;
    }

    /// <summary>Channels seen in Advertisement/LWRP traffic so far, or loaded from
    /// <c>config/livewire.yaml</c> if nothing's arrived yet this run. Safe to call from the UI thread;
    /// backed by a concurrent snapshot, not a live enumeration of the receive loop's own state.</summary>
    public IReadOnlyList<DiscoveredLivewireChannel> GetSnapshot() => _channels.Values.OrderBy(c => c.Number).ToList();

    /// <summary>Current health of the discovery process itself (NIC/socket state, last packet seen) —
    /// read on demand by the UI (same snapshot pattern as <see cref="GetSnapshot"/>, not a live push).</summary>
    public LivewireDiscoveryStatus GetStatus() => _status;

    /// <summary>Writes the current snapshot to <c>config/livewire.yaml</c> — called from the UI's
    /// "Обновить" button, not on every discovery, so a cache write doesn't happen dozens of times a
    /// minute just because Advertisement traffic is chatty. A cache is a convenience for the next
    /// startup, not a live log, so this being a point-in-time snapshot rather than continuously
    /// up to date is fine.</summary>
    public void SaveSnapshotToCache()
    {
        var cache = new LivewireCacheFile
        {
            Channels = _channels.Values.OrderBy(c => c.Number).Select(c => new LivewireCacheEntry
            {
                Number = c.Number,
                Name = c.Name,
                DeviceName = c.DeviceName,
                DeviceIp = c.DeviceIp?.ToString() ?? "",
                LastSeen = c.LastSeen.ToUnixTimeSeconds(),
            }).ToList(),
        };
        _configLoader.SaveLivewireCache(_configDir, cache);
    }

    private void LoadCacheFromDisk()
    {
        var cache = _configLoader.LoadLivewireCache(_configDir);
        foreach (var entry in cache.Channels)
        {
            if (!LivewireAddressing.IsValidChannelNumber(entry.Number)) continue;
            var deviceIp = string.IsNullOrEmpty(entry.DeviceIp) ? null : IPAddress.Parse(entry.DeviceIp);
            _channels[entry.Number] = new DiscoveredLivewireChannel(entry.Number, entry.Name, entry.DeviceName, deviceIp,
                LivewireAddressing.ChannelToMulticastAddress(entry.Number), DateTimeOffset.FromUnixTimeSeconds(entry.LastSeen));
        }
        if (cache.Channels.Count > 0)
            _logger.LogInformation("Livewire discovery: загружено {Count} каналов из кэша livewire.yaml", cache.Channels.Count);
    }

    /// <summary>Attempts the initial connect at hosted-service startup, using whatever NIC was already
    /// saved in <c>settings.yaml</c> — after this, NIC changes go through <see cref="ConnectAsync"/>
    /// (called from the App Settings dialog's toggle), not through restarting the app.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        LoadCacheFromDisk(); // seeds the picker even before any live traffic arrives (or if discovery is disabled below)
        await ConnectAsync(_appSettings.Current.LivewireNic);
    }

    /// <summary>Opens the Advertisement socket on <paramref name="nicId"/> (closing whatever was open
    /// first, so this doubles as "switch NIC" without a separate disconnect step) — called both at
    /// startup and from the App Settings dialog's "Подключить" button. Returns whether the socket is
    /// now listening; <see cref="GetStatus"/> carries the specific reason on failure.</summary>
    public async Task<bool> ConnectAsync(string nicId)
    {
        await _connectLock.WaitAsync();
        try
        {
            await CloseSocketAsync();
            return Connect(nicId);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Closes the Advertisement socket without touching the saved <c>livewire_nic</c> setting
    /// — called from the App Settings dialog's "Отключить" button. Purely a runtime/session action:
    /// the next app start still auto-connects using whatever NIC is saved, regardless of whether this
    /// was called in a previous run.</summary>
    public async Task DisconnectAsync()
    {
        await _connectLock.WaitAsync();
        try
        {
            await CloseSocketAsync();
            _status = new LivewireDiscoveryStatus(LivewireDiscoveryState.Disconnected, null, null, null);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _connectLock.WaitAsync(cancellationToken);
        try
        {
            await CloseSocketAsync(cancellationToken);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Synchronous socket-open attempt — sets <see cref="_status"/> to exactly one of
    /// Disabled/NicNotFound/SocketError/Listening and returns whether it ended up Listening. Callers
    /// (<see cref="ConnectAsync"/>, <see cref="StartAsync"/>) are responsible for closing any
    /// previously-open socket first via <see cref="CloseSocketAsync"/>.</summary>
    private bool Connect(string nicId)
    {
        if (string.IsNullOrEmpty(nicId))
        {
            _logger.LogInformation("Livewire discovery: сетевой интерфейс не выбран в настройках приложения — автообнаружение каналов отключено");
            _status = new LivewireDiscoveryStatus(LivewireDiscoveryState.Disabled, null, null, null);
            return false;
        }

        var nicIp = NetworkInterfaceEnumerator.ResolveNicIPv4(nicId);
        if (nicIp == null)
        {
            _logger.LogWarning("Livewire discovery: выбранный сетевой интерфейс ({Nic}) не найден или не имеет IPv4-адреса — автообнаружение каналов отключено", nicId);
            _status = new LivewireDiscoveryStatus(LivewireDiscoveryState.NicNotFound, null, null, null);
            return false;
        }

        try
        {
            _client = new UdpClient();
            _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _client.Client.Bind(new IPEndPoint(IPAddress.Any, AdvertisementPort));
            _client.JoinMulticastGroup(IPAddress.Parse(AdvertisementMulticastAddress), IPAddress.Parse(nicIp));

            _logger.LogInformation("Livewire discovery: слушаю {Address}:{Port} через интерфейс {Ip}",
                AdvertisementMulticastAddress, AdvertisementPort, nicIp);
            _status = new LivewireDiscoveryStatus(LivewireDiscoveryState.Listening, nicIp, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Livewire discovery: не удалось открыть UDP-сокет на порту {Port} через интерфейс {Ip} — проверьте, что порт не занят другим процессом",
                AdvertisementPort, nicIp);
            _status = new LivewireDiscoveryStatus(LivewireDiscoveryState.SocketError, nicIp, ex.Message, null);
            _client?.Dispose();
            _client = null;
            return false;
        }

        _cts = new CancellationTokenSource();
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_client, _cts.Token));
        return true;
    }

    /// <summary>Tears down whatever socket/receive-loop is currently active, if any — a no-op if
    /// nothing's open (safe to call unconditionally from <see cref="ConnectAsync"/> before every
    /// connect attempt). Does not touch <see cref="_status"/>; callers set it to whatever comes next.</summary>
    private async Task CloseSocketAsync(CancellationToken cancellationToken = default)
    {
        _cts?.Cancel();
        _client?.Close();
        if (_receiveTask != null)
        {
            try { await _receiveTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
            catch { /* best-effort shutdown */ }
        }
        _client?.Dispose();
        _client = null;
        _cts = null;
        _receiveTask = null;
    }

    private async Task ReceiveLoopAsync(UdpClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await client.ReceiveAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Livewire discovery: ошибка приёма пакета");
                continue;
            }

            HandlePacket(result.RemoteEndPoint, result.Buffer);
        }
    }

    private void HandlePacket(IPEndPoint from, byte[] payload)
    {
        _status = _status with { LastPacketAt = DateTimeOffset.UtcNow };

        foreach (var discovered in LivewireAdvertisementParser.Parse(payload, DateTimeOffset.UtcNow))
        {
            MergeChannel(discovered);
            _logger.LogDebug("Livewire discovery: канал {Number} \"{Name}\" ({Address}) — от {From}",
                discovered.Number, discovered.Name, discovered.MulticastAddress, from);
        }

        var nodeIp = from.Address.ToString();
        if (_lwrpQueriedNodes.TryAdd(nodeIp, 0))
            _ = EnrichNamesViaLwrpAsync(nodeIp);
    }

    /// <summary>Combines a freshly-parsed sighting with whatever's already known for that channel
    /// number — an empty <see cref="DiscoveredLivewireChannel.Name"/> or
    /// <see cref="DiscoveredLivewireChannel.DeviceName"/> in the new sighting never overwrites a
    /// non-empty value already on file (see class doc comment for why: not every burst repeats
    /// <c>ATRN</c>/<c>PSNM</c>).</summary>
    private void MergeChannel(DiscoveredLivewireChannel incoming)
    {
        _channels.AddOrUpdate(incoming.Number, _ => incoming, (_, existing) => Merge(existing, incoming));
    }

    /// <summary>Pure combine step, split out from <see cref="MergeChannel"/> so the "don't let an empty
    /// field overwrite a known one" rule is unit-testable without spinning up the whole hosted
    /// service.</summary>
    internal static DiscoveredLivewireChannel Merge(DiscoveredLivewireChannel existing, DiscoveredLivewireChannel incoming) =>
        incoming with
        {
            Name = string.IsNullOrEmpty(incoming.Name) ? existing.Name : incoming.Name,
            DeviceName = string.IsNullOrEmpty(incoming.DeviceName) ? existing.DeviceName : incoming.DeviceName,
            DeviceIp = incoming.DeviceIp ?? existing.DeviceIp,
        };

    /// <summary>Fire-and-forget, at most once per node IP for the lifetime of this service (see
    /// <see cref="_lwrpQueriedNodes"/>) — not every node speaks LWRP, and reconnecting repeatedly to
    /// one that does isn't worth the risk (see class doc comment), so a node that fails or times out
    /// simply never gets a name from this path, falling back to whatever Advertisement already gave it.</summary>
    private async Task EnrichNamesViaLwrpAsync(string nodeIp)
    {
        var names = await LwrpClient.QuerySourceNamesAsync(nodeIp, LwrpTimeout, _logger);
        foreach (var (number, name) in names)
        {
            if (!LivewireAddressing.IsValidChannelNumber(number)) continue;

            _channels.AddOrUpdate(number,
                _ => new DiscoveredLivewireChannel(number, name, "", IPAddress.Parse(nodeIp), LivewireAddressing.ChannelToMulticastAddress(number), DateTimeOffset.UtcNow),
                (_, existing) => string.IsNullOrEmpty(existing.Name)
                    ? existing with { Name = name, LastSeen = DateTimeOffset.UtcNow }
                    : existing);
        }
        if (names.Count > 0)
            _logger.LogDebug("Livewire discovery: LWRP {Host} — получено {Count} имён источников", nodeIp, names.Count);
    }
}
