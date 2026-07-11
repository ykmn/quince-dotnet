using Quince.Service.Audio.Livewire;
using Xunit;

namespace Quince.Service.Tests.Audio;

public class LwrpParserTests
{
    // Real "SRC" response captured from a live LWRP server (172.22.0.47:93, a PC running Axia's
    // "Livewire Windows Driver" — DEVN:"lwwd" in its VER response) — see LIVEWIRE.md.
    private const string RealSrcResponse = """
        BEGIN
        SRC 1 PSNM:"Novoe Expres" RTPE:0 RTPA:"239.192.0.1" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 2 PSNM:"DorogSAT" RTPE:0 RTPA:"239.192.0.2" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 3 PSNM:"Radio7SAT" RTPE:0 RTPA:"239.192.0.3" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 9 PSNM:"Retro PGM3" RTPE:0 RTPA:"239.192.0.9" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 10 PSNM:"" RTPE:0 RTPA:"239.192.0.10" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 24 PSNM:"Express80" RTPE:0 RTPA:"239.192.0.24" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        END
        """;

    [Fact]
    public void ParseSourceNames_RealResponse_ExtractsNamesKeyedByChannelNumber()
    {
        var result = LwrpParser.ParseSourceNames(RealSrcResponse);

        Assert.Equal("Novoe Expres", result[1]);
        Assert.Equal("DorogSAT", result[2]);
        Assert.Equal("Radio7SAT", result[3]);
        Assert.Equal("Retro PGM3", result[9]);
        Assert.Equal("Express80", result[24]);
    }

    [Fact]
    public void ParseSourceNames_EmptyPsnm_IsOmittedNotAddedAsBlankName()
    {
        var result = LwrpParser.ParseSourceNames(RealSrcResponse);

        Assert.False(result.ContainsKey(10));
    }

    [Fact]
    public void ParseSourceNames_NameWithSpaces_StaysOneValueDespiteSpaceTokenizing()
    {
        var result = LwrpParser.ParseSourceNames(RealSrcResponse);

        Assert.Equal("Novoe Expres", result[1]); // would break into "Novoe and Expres"" if quotes weren't respected
        Assert.Equal("Retro PGM3", result[9]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\nnot a real response\n")]
    [InlineData("BEGIN\nEND")]
    public void ParseSourceNames_NoUsableData_ReturnsEmpty(string response)
    {
        Assert.Empty(LwrpParser.ParseSourceNames(response));
    }
}
