namespace Aftermath.Logs;

using System.Text.RegularExpressions;

/// <summary>
/// The tolerant matcher used when a service's conversionPattern carries a directive outside
/// Log4NetPatternParser's supported table. Takes the leading "HH:mm:ss.fff", every leading
/// bracketed "[...]" group as one opaque blob, the first all-caps word as the level, and the
/// remainder as the message (§3.1). Callers must emit these events with
/// <c>Confidence.Inferred</c> and <c>Caveat = "log pattern not fully recognised"</c>.
///
/// The leading bracket segment is captured whole, unparsed, precisely because its role is
/// unknown: it might be a thread id, a session token, or both concatenated, and guessing wrong
/// would mis-redact. Phase 5 blanks it entirely rather than pseudonymising it.
/// </summary>
public static partial class FallbackLogParser
{
    [GeneratedRegex(
        @"\G(?<time>\d{2}:\d{2}:\d{2}\.\d{3})\s*(?<brackets>(?:\[[^\]]*\]\s*)*)(?<level>[A-Z]+)?\s*(?<message>.*)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();

    public static bool TryMatch(string line, out LogLineFields fields)
    {
        Match m = Pattern().Match(line, 0);
        if (!m.Success || m.Groups["time"].Length == 0)
        {
            fields = null!;
            return false;
        }

        string brackets = m.Groups["brackets"].Value.Trim();
        fields = new LogLineFields
        {
            TimeOfDay = m.Groups["time"].Value,
            LeadingBracketSegment = brackets.Length == 0 ? null : brackets,
            Level = m.Groups["level"] is { Success: true, Length: > 0 } level ? level.Value : null,
            Message = m.Groups["message"].Value,
        };
        return true;
    }
}
