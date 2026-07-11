using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Quince.Service.Audio.Livewire;

public readonly record struct DiscoveredLivewireChannel(int Number, string Name, string DeviceName, IPAddress? DeviceIp, IPAddress MulticastAddress, DateTimeOffset LastSeen);

/// <summary>
/// Parses a Livewire "Advertisement" multicast packet (239.192.255.3:4001, see
/// <see cref="LivewireDiscoveryService.AdvertisementPort"/> — not publicly documented, found by live
/// capture) into the sources it announces. Reverse-engineered from real traffic across several
/// Axia device families (xNode, Rio, and third-party bridges) — see <c>LIVEWIRE.md</c> at the repo root
/// for the full byte-level writeup and annotated samples this was derived from.
///
/// One packet describes one network node and, if it originates any Livewire sources, a list of them
/// (an xNode with 16 inputs sends all 16 in a single packet). A node with <c>NUMS=0</c> (a pure
/// listener/logger, no sources of its own) is valid and yields an empty list — not an error.
///
/// <see cref="DiscoveredLivewireChannel.Name"/> (the source's own <c>PSNM</c>) and
/// <see cref="DiscoveredLivewireChannel.DeviceName"/> (the node's <c>ATRN</c>) are independent —
/// neither falls back to the other here. Not every device sends <c>PSNM</c> at all (see below), so
/// <c>Name</c> can be empty while <c>DeviceName</c> is still known; callers (the UI) decide how to
/// present that, this parser doesn't invent a channel name out of the device name.
///
/// <see cref="DiscoveredLivewireChannel.DeviceIp"/> comes from the node's own <c>INIP</c> field — the
/// node's unicast address on the Livewire network, not the multicast address a source streams on
/// (<see cref="DiscoveredLivewireChannel.MulticastAddress"/>) — so the UI can show which physical
/// device a channel actually lives on.
/// </summary>
public static class LivewireAdvertisementParser
{
    private static readonly byte[] Magic = [0x03, 0x00, 0x02, 0x07];
    private const int HeaderSize = 16;

    /// <summary>Returns every source found in this packet (zero, one, or many). <paramref name="now"/>
    /// is threaded through explicitly (rather than read from the clock inside) so this stays a pure,
    /// unit-testable function of its input bytes.</summary>
    public static IReadOnlyList<DiscoveredLivewireChannel> Parse(byte[] payload, DateTimeOffset now)
    {
        if (payload.Length < HeaderSize || !payload.AsSpan(0, 4).SequenceEqual(Magic))
            return [];

        var results = new List<DiscoveredLivewireChannel>();
        string? deviceName = null; // ATRN — the node's own model/name, e.g. "EP-xNode-V"
        IPAddress? deviceIp = null; // INIP — the node's own unicast address on the Livewire network
        int? currentPsid = null;
        string? currentName = null;

        void FlushCurrentSource()
        {
            if (currentPsid is int psid && LivewireAddressing.IsValidChannelNumber(psid))
            {
                results.Add(new DiscoveredLivewireChannel(psid, currentName ?? "", deviceName ?? "", deviceIp,
                    LivewireAddressing.ChannelToMulticastAddress(psid), now));
            }
            currentPsid = null;
            currentName = null;
        }

        var offset = HeaderSize;
        while (offset + 4 <= payload.Length)
        {
            // End-of-source-list sentinel: tag bytes FF FF FF FF / FF FF FF FE (never valid ASCII).
            if (payload[offset] == 0xFF && payload[offset + 1] == 0xFF && payload[offset + 2] == 0xFF &&
                (payload[offset + 3] == 0xFF || payload[offset + 3] == 0xFE))
                break;

            if (offset + 5 > payload.Length) break;
            var tag = Encoding.ASCII.GetString(payload, offset, 4);
            var type = payload[offset + 4];
            offset += 5;

            int valueLength;
            if (type == 0x03) // string: 2-byte big-endian length prefix, then that many null-padded bytes
            {
                if (offset + 2 > payload.Length) break;
                valueLength = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
                offset += 2;
            }
            else
            {
                valueLength = FixedValueLength(type);
                if (valueLength < 0) break; // unknown field type — can't safely locate the next tag
            }

            if (offset + valueLength > payload.Length) break; // truncated packet

            if (IsSourceTag(tag))
            {
                FlushCurrentSource(); // "S001", "S002".. starts a new source block
            }
            else if (tag == "PSID" && valueLength == 4)
            {
                currentPsid = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
            }
            else if (tag == "PSNM")
            {
                currentName = DecodeNullPaddedAscii(payload, offset, valueLength);
            }
            else if (tag == "ATRN")
            {
                deviceName = DecodeNullPaddedAscii(payload, offset, valueLength);
            }
            else if (tag == "INIP" && valueLength == 4)
            {
                deviceIp = new IPAddress(payload.AsSpan(offset, 4));
            }

            offset += valueLength;
        }

        FlushCurrentSource();
        return results;
    }

    /// <summary>Value size in bytes for the fixed-size field types seen in captures. String fields
    /// (type 0x03) are handled separately since their length is carried in the packet itself.</summary>
    private static int FixedValueLength(byte type) => type switch
    {
        0x00 or 0x07 => 1,
        0x06 or 0x08 => 2,
        0x01 => 4,
        0x09 => 8,
        _ => -1,
    };

    private static bool IsSourceTag(string tag) =>
        tag.Length == 4 && tag[0] == 'S' && char.IsAsciiDigit(tag[1]) && char.IsAsciiDigit(tag[2]) && char.IsAsciiDigit(tag[3]);

    private static string DecodeNullPaddedAscii(byte[] payload, int offset, int length)
    {
        var end = offset;
        var max = offset + length;
        while (end < max && payload[end] != 0) end++;
        return Encoding.ASCII.GetString(payload, offset, end - offset);
    }
}
