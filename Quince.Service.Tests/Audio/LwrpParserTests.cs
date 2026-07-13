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

    // Reproduces the field report (docs/HISTORY.md): a node with a large configured source table (216
    // slots in the real report) whose firmware defaults a never-renamed slot's PSNM to a synthetic
    // "<prefix> <own slot number>" label instead of leaving it empty (contrast RealSrcResponse's slot
    // 10 above, from a *different* real node that does leave it empty) — mixed in with a handful of
    // genuinely real, meaningfully-named sources.
    private const string BugReportSrcResponse = """
        BEGIN
        SRC 1 PSNM:"SRC 1" RTPE:0 RTPA:"239.192.0.1" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 9 PSNM:"RR-intelsat" RTPE:0 RTPA:"239.192.0.9" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 10 PSNM:"R7-Yamal401" RTPE:0 RTPA:"239.192.0.10" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 12 PSNM:"PC 12" RTPE:0 RTPA:"239.192.0.12" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 24 PSNM:"R7-intelsat" RTPE:0 RTPA:"239.192.0.24" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        END
        """;

    [Fact]
    public void ParseSourceNames_DefaultPlaceholderNames_AreExcluded()
    {
        var result = LwrpParser.ParseSourceNames(BugReportSrcResponse);

        Assert.False(result.ContainsKey(1));  // PSNM:"SRC 1" on slot 1
        Assert.False(result.ContainsKey(12)); // PSNM:"PC 12" on slot 12
    }

    [Fact]
    public void ParseSourceNames_RealNamesResemblingButNotMatchingPlaceholder_AreKept()
    {
        var result = LwrpParser.ParseSourceNames(BugReportSrcResponse);

        Assert.Equal("RR-intelsat", result[9]);
        Assert.Equal("R7-Yamal401", result[10]); // trailing digits "401" != slot 10 — not a placeholder
        Assert.Equal("R7-intelsat", result[24]);
    }

    // Reproduces a second, independent real-world discrepancy (docs/HISTORY.md #130): confirmed via
    // this same network's own Advertisement-sourced cache data that device 172.22.0.43 ("air-inet1")
    // really is channels 6501-6507 for these exact names — but that device's LWRP table's leading
    // "SRC <n>" index is a node-LOCAL positional slot (1..7), completely unrelated to the source's
    // real, globally-routable Livewire channel number, which RTPA correctly encodes.
    private const string LocalIndexMismatchSrcResponse = """
        BEGIN
        SRC 1 PSNM:"EP-Top" RTPE:1 RTPA:"239.192.25.101" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 2 PSNM:"EP-New" RTPE:1 RTPA:"239.192.25.102" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        SRC 7 PSNM:"Retro70" RTPE:1 RTPA:"239.192.25.107" INGN:-120 NCHN:2 RTPP:240 TXTO:250
        END
        """;

    [Fact]
    public void ParseSourceNames_LocalIndexDiffersFromRtpa_UsesRtpaDerivedChannelNumber()
    {
        var result = LwrpParser.ParseSourceNames(LocalIndexMismatchSrcResponse);

        Assert.False(result.ContainsKey(1));
        Assert.False(result.ContainsKey(2));
        Assert.False(result.ContainsKey(7));
        Assert.Equal("EP-Top", result[6501]);
        Assert.Equal("EP-New", result[6502]);
        Assert.Equal("Retro70", result[6507]);
    }

    [Fact]
    public void ParseSourceNames_MissingRtpa_FallsBackToLeadingIndex()
    {
        // Not observed in any real capture so far, but RTPA isn't guaranteed present on every device —
        // degrade to the old behavior instead of silently dropping the source.
        const string response = """
            BEGIN
            SRC 5 PSNM:"NoRtpaHere" INGN:-120 NCHN:2 RTPP:240 TXTO:250
            END
            """;

        var result = LwrpParser.ParseSourceNames(response);

        Assert.Equal("NoRtpaHere", result[5]);
    }

    [Fact]
    public void ParseSourceNames_UnparsableRtpa_FallsBackToLeadingIndex()
    {
        const string response = """
            BEGIN
            SRC 5 PSNM:"BadRtpa" RTPA:"not-an-ip" INGN:-120 NCHN:2 RTPP:240 TXTO:250
            END
            """;

        var result = LwrpParser.ParseSourceNames(response);

        Assert.Equal("BadRtpa", result[5]);
    }

    [Theory]
    [InlineData("SRC 1", 1, true)]           // exact reported bug pattern
    [InlineData("PC 12", 12, true)]          // exact reported bug pattern
    [InlineData("PC12", 12, true)]           // no space between prefix and number — still a placeholder
    [InlineData("SRC 01", 1, true)]          // leading zero in trailing digits, still matches slot 1
    [InlineData("RR-intelsat", 9, false)]    // real name, no trailing digits at all
    [InlineData("R7-intelsat", 24, false)]   // real name, no trailing digits at all
    [InlineData("R7-Yamal401", 10, false)]   // real name; trailing digits (401) present but != own slot (10)
    [InlineData("SRC 1", 2, false)]          // trailing digits match a DIFFERENT slot's number, not this line's own
    [InlineData("Novoe Expres", 1, false)]   // real sample name (RealSrcResponse above)
    [InlineData("DorogSAT", 2, false)]       // real sample name
    [InlineData("Radio7SAT", 3, false)]      // real sample name
    [InlineData("Retro PGM3", 9, false)]     // real sample name; ends in digit "3" but slot is 9
    [InlineData("Express80", 24, false)]     // real sample name; ends in "80" but slot is 24
    [InlineData("12", 12, false)]            // bare numeric name, no prefix — deliberately NOT filtered
    [InlineData("SRC1-East", 1, false)]      // ends in "t" — no TRAILING digits at all, excluded by construction
    [InlineData("", 1, false)]               // empty name — defensive; ParseSourceNames never calls this on empty
    public void LooksLikeDefaultPlaceholderName_ReturnsExpected(string name, int number, bool expected)
    {
        Assert.Equal(expected, LwrpParser.LooksLikeDefaultPlaceholderName(name, number));
    }
}
