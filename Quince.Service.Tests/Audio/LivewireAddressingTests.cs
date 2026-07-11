using System.Net;
using Quince.Service.Audio;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LivewireAddressingTests
{
    // Reference values confirmed against public Axia/Telos documentation and third-party
    // conversion tools (github.com/anthonyeden/Axia-Livewire-Stream-Address-Helper,
    // docs.telosalliance.com "Calculating a multicast address from Livewire channel number").
    [Theory]
    [InlineData(27, "239.192.0.27")]
    [InlineData(1212, "239.192.4.188")]
    [InlineData(1000, "239.192.3.232")]
    [InlineData(1, "239.192.0.1")]
    [InlineData(65535, "239.192.255.255")]
    public void ChannelToMulticastAddress_MatchesDocumentedExamples(int channel, string expected)
    {
        var address = LivewireAddressing.ChannelToMulticastAddress(channel);
        Assert.Equal(expected, address.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void ChannelToMulticastAddress_OutOfRange_Throws(int channel)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LivewireAddressing.ChannelToMulticastAddress(channel));
    }

    [Theory]
    [InlineData("239.192.0.27", 27)]
    [InlineData("239.192.4.188", 1212)]
    public void MulticastAddressToChannel_IsInverseOfChannelToMulticastAddress(string address, int expectedChannel)
    {
        var result = LivewireAddressing.MulticastAddressToChannel(IPAddress.Parse(address));
        Assert.Equal(expectedChannel, result);
    }

    [Theory]
    [InlineData("239.193.0.27")] // backfeed range, not a "From" source
    [InlineData("10.0.0.1")]
    public void MulticastAddressToChannel_OutsideFromRange_ReturnsNull(string address)
    {
        var result = LivewireAddressing.MulticastAddressToChannel(IPAddress.Parse(address));
        Assert.Null(result);
    }
}
