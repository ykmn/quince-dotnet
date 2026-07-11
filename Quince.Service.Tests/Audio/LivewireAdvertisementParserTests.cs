using Quince.Service.Audio.Livewire;
using Xunit;

namespace Quince.Service.Tests.Audio;

/// <summary>
/// Fixtures are raw Ethernet frames exactly as captured with Wireshark ("Copy → ...as Hex Dump") on a
/// real Livewire network — see LIVEWIRE.md at the repo root for the full protocol writeup these were
/// reverse-engineered from. <see cref="RawFrame"/> strips the Ethernet+IPv4+UDP headers (42 bytes,
/// standard case: no IP options) to get the UDP payload the parser actually consumes.
/// </summary>
public class LivewireAdvertisementParserTests
{
    private const int UdpPayloadOffset = 42; // Ethernet(14) + IPv4 without options(20) + UDP(8)

    private static byte[] RawFrame(string hexDump)
    {
        var bytes = new List<byte>();
        foreach (var line in hexDump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tokens = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens.Skip(1)) // skip the leading offset column
                bytes.Add(Convert.ToByte(token, 16));
        }
        return bytes.Skip(UdpPayloadOffset).ToArray();
    }

    // Device 172.22.0.49 — short heartbeat variant: has NUMS=1 but no source (S0NN) blocks in this
    // particular packet. Real devices alternate between this and the full burst below.
    private const string ShortHeartbeatNoSources = """
        0000   01 00 5e 40 ff 03 00 13 95 19 bb d3 08 00 45 00
        0010   00 73 10 dd 40 00 80 11 4e 91 ac 16 00 31 ef c0
        0020   ff 03 96 69 0f a1 00 5f 55 ec 03 00 02 07 38 53
        0030   c7 d2 00 00 00 00 00 00 00 00 4e 45 53 54 00 03
        0040   50 56 45 52 08 00 02 41 44 56 54 07 02 54 45 52
        0050   4d 06 00 2d 49 4e 44 49 00 05 41 44 56 56 01 00
        0060   00 00 1b 48 57 49 44 08 00 31 49 4e 49 50 01 ac
        0070   16 00 31 55 44 50 43 08 0f a0 4e 55 4d 53 08 00
        0080   01
        """;

    // Device 172.22.0.47 ("emg-logger1") — NUMS=0, pure listener with no sources of its own.
    private const string ZeroSourcesWithDeviceName = """
        0000   01 00 5e 40 ff 03 d0 94 66 48 3c 02 08 00 45 00
        0010   00 9a 6b 70 00 00 80 11 00 00 ac 16 00 2f ef c0
        0020   ff 03 df 09 0f a1 00 86 9b a1 03 00 02 07 00 65
        0030   dd a0 00 00 00 00 00 00 00 00 4e 45 53 54 00 03
        0040   50 56 45 52 08 00 02 41 44 56 54 07 01 54 45 52
        0050   4d 06 00 54 49 4e 44 49 00 06 41 44 56 56 01 00
        0060   00 00 1a 48 57 49 44 08 00 2f 49 4e 49 50 01 ac
        0070   16 00 2f 55 44 50 43 08 0f a0 4e 55 4d 53 08 00
        0080   00 41 54 52 4e 03 00 20 65 6d 67 2d 6c 6f 67 67
        0090   65 72 31 00 00 00 00 00 00 00 00 00 00 00 00 00
        00a0   00 00 00 00 00 00 00 00
        """;

    // Device 172.22.2.14 ("EP-xNode-V") — full burst, two named sources.
    private const string TwoNamedSources = """
        0000   01 00 5e 40 ff 03 00 50 c2 90 11 65 08 00 45 00
        0010   01 72 00 00 40 00 80 11 5c 92 ac 16 02 0e ef c0
        0020   ff 03 d8 94 0f a1 01 5e df c9 03 00 02 07 c6 18
        0030   d3 45 00 00 00 00 00 00 00 00 4e 45 53 54 00 05
        0040   50 56 45 52 08 00 02 41 44 56 54 07 01 54 45 52
        0050   4d 06 00 54 49 4e 44 49 00 06 41 44 56 56 01 00
        0060   00 00 0c 48 57 49 44 08 02 0e 49 4e 49 50 01 ac
        0070   16 02 0e 55 44 50 43 08 0f a0 4e 55 4d 53 08 00
        0080   02 41 54 52 4e 03 00 20 45 50 2d 78 4e 6f 64 65
        0090   2d 56 00 00 00 00 00 00 00 00 00 00 00 00 00 00
        00a0   00 00 00 00 00 00 00 00 53 30 30 31 06 00 65 49
        00b0   4e 44 49 00 0b 50 53 49 44 01 00 00 08 0d 53 48
        00c0   41 42 07 00 46 53 49 44 01 ef c0 08 0d 46 41 53
        00d0   54 07 02 46 41 53 4d 07 01 42 53 49 44 01 ef c1
        00e0   08 0d 42 41 53 54 07 01 42 41 53 4d 07 00 4c 50
        00f0   49 44 01 00 00 08 0d 53 54 50 4c 07 00 50 53 4e
        0100   4d 03 00 10 66 72 6f 6d 20 41 54 45 4d 00 00 00
        0110   00 00 00 00 53 30 30 33 06 00 65 49 4e 44 49 00
        0120   0b 50 53 49 44 01 00 00 08 0f 53 48 41 42 07 00
        0130   46 53 49 44 01 ef c0 08 0f 46 41 53 54 07 02 46
        0140   41 53 4d 07 01 42 53 49 44 01 ef c1 08 0f 42 41
        0150   53 54 07 01 42 41 53 4d 07 00 4c 50 49 44 01 00
        0160   00 08 0f 53 54 50 4c 07 00 50 53 4e 4d 03 00 10
        0170   4a 41 4d 55 4c 55 53 00 00 00 00 00 00 00 00 00
        """;

    // Device 172.22.0.42 — full burst, 15 "lite" sources (S001..S010, S012..S016 — S011 skipped by the
    // device itself): only INDI+PSID+BUSY per source, no PSNM and no device-level ATRN either.
    private const string FifteenUnnamedSources = """
        0000   01 00 5e 40 ff 03 9c b6 54 8c 7f 5c 08 00 45 00
        0010   02 7a 27 0e 00 00 80 11 76 60 ac 16 00 2a ef c0
        0020   ff 03 c2 c3 0f a1 02 66 75 ac 03 00 02 07 00 37
        0030   3b 6a 6c 65 64 2e 0a 54 68 69 4e 45 53 54 00 14
        0040   50 56 45 52 08 00 02 41 44 56 54 07 03 54 45 52
        0050   4d 06 00 0d 49 4e 44 49 00 01 48 57 49 44 08 00
        0060   2a 53 30 30 31 06 00 1c 49 4e 44 49 00 02 50 53
        0070   49 44 01 00 00 0f e1 42 55 53 59 09 00 00 00 00
        0080   00 00 00 00 53 30 30 32 06 00 1c 49 4e 44 49 00
        0090   02 50 53 49 44 01 00 00 0f e2 42 55 53 59 09 00
        00a0   00 00 00 00 00 00 00 53 30 30 33 06 00 1c 49 4e
        00b0   44 49 00 02 50 53 49 44 01 00 00 0f e3 42 55 53
        00c0   59 09 00 00 00 00 00 00 00 00 53 30 30 34 06 00
        00d0   1c 49 4e 44 49 00 02 50 53 49 44 01 00 00 0f e4
        00e0   42 55 53 59 09 00 00 00 00 00 00 00 00 53 30 30
        00f0   35 06 00 1c 49 4e 44 49 00 02 50 53 49 44 01 00
        0100   00 0f e5 42 55 53 59 09 00 00 00 00 00 00 00 00
        0110   53 30 30 36 06 00 1c 49 4e 44 49 00 02 50 53 49
        0120   44 01 00 00 0f e6 42 55 53 59 09 00 00 00 00 00
        0130   00 00 00 53 30 30 37 06 00 1c 49 4e 44 49 00 02
        0140   50 53 49 44 01 00 00 0f e7 42 55 53 59 09 00 00
        0150   00 00 00 00 00 00 53 30 30 38 06 00 1c 49 4e 44
        0160   49 00 02 50 53 49 44 01 00 00 0f e8 42 55 53 59
        0170   09 00 00 00 00 00 00 00 00 53 30 30 39 06 00 1c
        0180   49 4e 44 49 00 02 50 53 49 44 01 00 00 0f e9 42
        0190   55 53 59 09 00 00 00 00 00 00 00 00 53 30 31 30
        01a0   06 00 1c 49 4e 44 49 00 02 50 53 49 44 01 00 00
        01b0   0f ea 42 55 53 59 09 00 00 00 00 00 00 00 00 53
        01c0   30 31 32 06 00 1c 49 4e 44 49 00 02 50 53 49 44
        01d0   01 00 00 0f ec 42 55 53 59 09 00 00 00 00 00 00
        01e0   00 00 53 30 31 33 06 00 1c 49 4e 44 49 00 02 50
        01f0   53 49 44 01 00 00 0f ed 42 55 53 59 09 00 00 00
        0200   00 00 00 00 00 53 30 31 34 06 00 1c 49 4e 44 49
        0210   00 02 50 53 49 44 01 00 00 0f ee 42 55 53 59 09
        0220   00 00 00 00 00 00 00 00 53 30 31 35 06 00 1c 49
        0230   4e 44 49 00 02 50 53 49 44 01 00 00 0f ef 42 55
        0240   53 59 09 00 00 00 00 00 00 00 00 53 30 31 36 06
        0250   00 1c 49 4e 44 49 00 02 50 53 49 44 01 00 00 0f
        0260   f0 42 55 53 59 09 00 00 00 00 00 00 00 00 ff ff
        0270   ff ff 09 00 00 00 00 00 00 00 00 ff ff ff fe 09
        0280   00 00 00 00 00 00 00 00
        """;

    private static readonly DateTimeOffset Now = new(2026, 7, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_ShortHeartbeatWithoutSourceBlocks_ReturnsEmpty()
    {
        var result = LivewireAdvertisementParser.Parse(RawFrame(ShortHeartbeatNoSources), Now);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_ZeroSourceDevice_ReturnsEmpty()
    {
        var result = LivewireAdvertisementParser.Parse(RawFrame(ZeroSourcesWithDeviceName), Now);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_TwoNamedSources_ReturnsBothWithNamesFromPSNM()
    {
        var result = LivewireAdvertisementParser.Parse(RawFrame(TwoNamedSources), Now);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, c => c.Number == 2061 && c.Name == "from ATEM" && c.MulticastAddress.ToString() == "239.192.8.13");
        Assert.Contains(result, c => c.Number == 2063 && c.Name == "JAMULUS" && c.MulticastAddress.ToString() == "239.192.8.15");
        Assert.All(result, c => Assert.Equal("EP-xNode-V", c.DeviceName)); // ATRN, independent of the per-source PSNM name
        Assert.All(result, c => Assert.Equal(System.Net.IPAddress.Parse("172.22.2.14"), c.DeviceIp)); // INIP — the node's own address, not the multicast source address
    }

    [Fact]
    public void Parse_SourcesWithoutPsnm_NameAndDeviceNameBothEmpty()
    {
        var result = LivewireAdvertisementParser.Parse(RawFrame(FifteenUnnamedSources), Now);

        Assert.Equal(15, result.Count);
        Assert.All(result, c => Assert.Equal("", c.Name)); // no PSNM in this packet
        Assert.All(result, c => Assert.Equal("", c.DeviceName)); // no ATRN in this packet either
        Assert.All(result, c => Assert.Null(c.DeviceIp)); // no INIP in this packet either
        Assert.Contains(result, c => c.Number == 4065); // S001
        Assert.Contains(result, c => c.Number == 4080); // S016
        Assert.DoesNotContain(result, c => c.Number == 4075); // S011 was skipped by the device itself
    }

    [Fact]
    public void Parse_GarbageOrTooShortPayload_ReturnsEmptyWithoutThrowing()
    {
        Assert.Empty(LivewireAdvertisementParser.Parse([], Now));
        Assert.Empty(LivewireAdvertisementParser.Parse([1, 2, 3], Now));
        Assert.Empty(LivewireAdvertisementParser.Parse(new byte[100], Now)); // all zeros, wrong magic
    }
}
