using System.Text;

namespace Quince.Service.Audio.Livewire;

/// <summary>
/// Parses responses from the Livewire Routing Protocol (LWRP, TCP port 93) — publicly known via
/// working open-source clients (e.g. github.com/anthonyeden/Livewire-Routing-Protocol-Client), and
/// confirmed against a real "SRC" response captured on this project's network (see <c>LIVEWIRE.md</c>).
///
/// A "SRC" command's response looks like:
/// <code>
/// BEGIN
/// SRC 1 PSNM:"Novoe Expres" RTPE:0 RTPA:"239.192.0.1" INGN:-120 NCHN:2 RTPP:240 TXTO:250
/// SRC 2 PSNM:"DorogSAT" RTPE:0 RTPA:"239.192.0.2" INGN:-120 NCHN:2 RTPP:240 TXTO:250
/// END
/// </code>
/// One line per source: <c>SRC &lt;channel number&gt; &lt;KEY&gt;:&lt;value&gt; ...</c>, string values
/// double-quoted (may contain spaces, may be empty). The channel number matches this app's own
/// <see cref="LivewireAddressing"/> scheme directly — confirmed against <c>RTPA</c> (the source's own
/// multicast address) on every line of the real sample this was built from.
/// </summary>
public static class LwrpParser
{
    /// <summary>Returns channel number → name for every source in the response that has a non-empty
    /// <c>PSNM</c>. A present-but-empty <c>PSNM:""</c> (an unconfigured slot — seen in real traffic) is
    /// correctly treated as "no name", not included in the result — same "don't invent a name" rule as
    /// <see cref="LivewireAdvertisementParser"/>.</summary>
    public static Dictionary<int, string> ParseSourceNames(string response)
    {
        var result = new Dictionary<int, string>();
        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("SRC ", StringComparison.Ordinal)) continue;

            var tokens = Tokenize(line);
            if (tokens.Count < 2 || !int.TryParse(tokens[1], out var number)) continue;
            if (!LivewireAddressing.IsValidChannelNumber(number)) continue;

            foreach (var token in tokens.Skip(2))
            {
                var separator = token.IndexOf(':');
                if (separator < 0) continue;
                if (token[..separator] != "PSNM") continue;

                var value = token[(separator + 1)..].Trim('"');
                if (!string.IsNullOrEmpty(value))
                    result[number] = value;
            }
        }
        return result;
    }

    /// <summary>Splits a line on spaces, except spaces inside double-quoted values (so
    /// <c>PSNM:"Two Words"</c> stays one token) — the same quoting rule used by the open-source LWRP
    /// clients this was cross-checked against.</summary>
    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; current.Append(ch); continue; }
            if (ch == ' ' && !inQuotes)
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }
}
