using System.Net;
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
/// One line per source: <c>SRC &lt;index&gt; &lt;KEY&gt;:&lt;value&gt; ...</c>, string values
/// double-quoted (may contain spaces, may be empty). The leading <c>&lt;index&gt;</c> happened to match
/// this app's own <see cref="LivewireAddressing"/> channel-number scheme on the one real sample this
/// parser was originally built from — but a second real device (see field report, docs/HISTORY.md #130)
/// showed this is only a coincidence, not a protocol guarantee: that node's LWRP table uses a
/// node-local positional index there (e.g. 1..16) that's completely different from the source's real,
/// globally-routable Livewire channel number. <c>RTPA</c> — the source's own multicast address,
/// <c>239.192.X.Y</c> encoding channel <c>N</c> by construction — is the one field that's authoritative
/// by definition (it's literally where the audio streams), so <see cref="ParseSourceNames"/> derives the
/// channel number from <c>RTPA</c>, falling back to the leading index only if <c>RTPA</c> is missing or
/// unparsable.
/// </summary>
public static class LwrpParser
{
    /// <summary>Returns channel number → name for every source in the response that has a non-empty,
    /// non-placeholder <c>PSNM</c>. The channel number comes from <c>RTPA</c> (decoded back to a channel
    /// number via <see cref="LivewireAddressing.MulticastAddressToChannel"/>), not the line's leading
    /// <c>SRC &lt;index&gt;</c> value — see class doc comment for why trusting the leading index alone
    /// produced wrong numbers on real hardware (docs/HISTORY.md #130). Falls back to the leading index
    /// if <c>RTPA</c> is absent or fails to parse, so a device that omits it (not observed, but not
    /// guaranteed present either) still yields something instead of silently dropping the source.
    ///
    /// A present-but-empty <c>PSNM:""</c> (an unconfigured slot — seen in real traffic, LIVEWIRE.md §3)
    /// is correctly treated as "no name", not included in the result — same "don't invent a name" rule
    /// as <see cref="LivewireAdvertisementParser"/>. Also excludes a <c>PSNM</c> that's just an
    /// auto-generated default placeholder for a never-renamed slot — a
    /// <c>&lt;prefix&gt;&lt;this line's own channel number&gt;</c> pattern (e.g. <c>"SRC 1"</c> on
    /// channel 1, <c>"PC 12"</c> on channel 12) seen in real traffic from a node whose firmware defaults
    /// an unconfigured slot's name to a synthetic label instead of leaving <c>PSNM</c> empty (contrast
    /// the "lwwd" driver sample in LIVEWIRE.md §3, which leaves an unused slot's <c>PSNM</c> empty
    /// instead) — see <see cref="LooksLikeDefaultPlaceholderName"/>. Without this, a node with a large
    /// configured source table (216 slots, in the field report that prompted this, docs/HISTORY.md #129)
    /// floods the discovered-channels list with mostly-meaningless placeholder entries.</summary>
    public static Dictionary<int, string> ParseSourceNames(string response)
    {
        var result = new Dictionary<int, string>();
        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("SRC ", StringComparison.Ordinal)) continue;

            var tokens = Tokenize(line);
            if (tokens.Count < 2 || !int.TryParse(tokens[1], out var localIndex)) continue;

            string? psnm = null;
            int? rtpaNumber = null;
            foreach (var token in tokens.Skip(2))
            {
                var separator = token.IndexOf(':');
                if (separator < 0) continue;
                var key = token[..separator];
                var value = token[(separator + 1)..].Trim('"');

                if (key == "PSNM")
                    psnm = value;
                else if (key == "RTPA" && IPAddress.TryParse(value, out var address))
                    rtpaNumber = LivewireAddressing.MulticastAddressToChannel(address);
            }

            var number = rtpaNumber ?? localIndex;
            if (!LivewireAddressing.IsValidChannelNumber(number)) continue;

            if (!string.IsNullOrEmpty(psnm) && !LooksLikeDefaultPlaceholderName(psnm, number))
                result[number] = psnm;
        }
        return result;
    }

    /// <summary>True if <paramref name="name"/> is just an auto-generated default placeholder for slot
    /// <paramref name="number"/> — a non-numeric prefix immediately followed by that exact same slot
    /// number (optionally space-separated), e.g. <c>"SRC 1"</c>/<c>"SRC1"</c> for slot 1, <c>"PC 12"</c>
    /// for slot 12. Deliberately does NOT flag two other shapes, both seen in real field data:
    /// <list type="bullet">
    /// <item>a bare numeric name with no prefix at all (e.g. <c>"12"</c>) — more plausibly a real, if
    /// terse, human label than a coincidental match on a template;</item>
    /// <item>a name whose trailing digits don't match THIS line's own slot number (e.g.
    /// <c>"R7-Yamal401"</c> on slot 10 — <c>401 != 10</c>) — comparing against the actual parsed number,
    /// not just "ends in some digit", is what keeps real names like this one and avoids false positives.</item>
    /// </list>
    /// This is a heuristic (no LWRP field reliably marks "this slot was never configured") rather than a
    /// guaranteed-correct rule, but it matches every placeholder name and no real name seen in the field
    /// report or LIVEWIRE.md's own captured sample.</summary>
    internal static bool LooksLikeDefaultPlaceholderName(string name, int number)
    {
        var trimmed = name.Trim();
        var digitsStart = trimmed.Length;
        while (digitsStart > 0 && char.IsAsciiDigit(trimmed[digitsStart - 1])) digitsStart--;
        if (digitsStart == trimmed.Length) return false; // no trailing digits at all — never a placeholder

        var digits = trimmed[digitsStart..];
        var prefix = trimmed[..digitsStart].TrimEnd();
        if (prefix.Length == 0) return false; // bare numeric name, e.g. "12" — leave alone

        return int.Parse(digits) == number;
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
