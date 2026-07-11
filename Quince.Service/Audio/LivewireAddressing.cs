using System.Net;

namespace Quince.Service.Audio;

/// <summary>
/// Pure conversion between a Livewire channel number and its multicast address, per the standard
/// Axia/Telos addressing scheme confirmed via public documentation (docs.telosalliance.com,
/// github.com/kylophone/a-look-at-livewire): "From" (source) channels live at 239.192.0.0 + N,
/// i.e. 239.192.(N div 256).(N mod 256), for N in 1..65535 (Axia's own tools cap the usable range
/// at 32767). "To"/backfeed channels (239.193.x.x) and surround (239.196.x.x) are out of scope —
/// this app only records a source, never a backfeed/mix-minus.
/// </summary>
public static class LivewireAddressing
{
    public const int MinChannelNumber = 1;
    public const int MaxChannelNumber = 65535;

    /// <summary>Standard RTP audio port for every Livewire stream, regardless of channel number.</summary>
    public const int AudioPort = 5004;

    public static bool IsValidChannelNumber(int channelNumber) =>
        channelNumber is >= MinChannelNumber and <= MaxChannelNumber;

    public static IPAddress ChannelToMulticastAddress(int channelNumber)
    {
        if (!IsValidChannelNumber(channelNumber))
            throw new ArgumentOutOfRangeException(nameof(channelNumber), channelNumber,
                $"Livewire channel number must be between {MinChannelNumber} and {MaxChannelNumber}");

        var high = (byte)((channelNumber >> 8) & 0xFF);
        var low = (byte)(channelNumber & 0xFF);
        return new IPAddress(new byte[] { 239, 192, high, low });
    }

    /// <summary>Inverse of <see cref="ChannelToMulticastAddress"/> — null if the address isn't in the
    /// 239.192.x.x "From" range (e.g. it's a backfeed/surround/unrelated multicast address).</summary>
    public static int? MulticastAddressToChannel(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4 || bytes[0] != 239 || bytes[1] != 192) return null;
        return (bytes[2] << 8) | bytes[3];
    }
}
