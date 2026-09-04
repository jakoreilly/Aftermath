namespace Aftermath.Rendering;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Renders events into mermaid's `timeline` diagram type, which GitLab, Confluence and Claude
/// artifacts all render natively with no library, and degrades to readable text where it does
/// not (§5.2).
///
/// GOTCHA: `:` separates fields in a timeline entry, and `;`, `#` and newlines break the block
/// outright — and log messages contain all of them. Every interpolated string is escaped
/// before it reaches the diagram; an unescaped exception message would otherwise silently
/// produce an unrenderable diagram, which looks like the tool crashed rather than like a
/// formatting bug.
/// </summary>
public static partial class MermaidTimeline
{
    private const int MaxEntryLength = 80;

    [GeneratedRegex(@"[:;#]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SpecialCharacter();

    public static string Render(IReadOnlyList<TimelineEvent> events, DateTimeOffset incidentAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("timeline");
        sb.AppendLine(CultureInfo.InvariantCulture, $"    title Incident {incidentAt:yyyy-MM-dd HH:mm} UTC");

        string? currentSection = null;
        foreach (TimelineEvent e in events)
        {
            string section = SectionFor(e.At, incidentAt);
            if (!string.Equals(section, currentSection, StringComparison.Ordinal))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"    section {section}");
                currentSection = section;
            }

            string entry = Escape($"{e.Service} — {e.Summary}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"        {e.At:HH:mm} : {entry}");
        }

        return sb.ToString();
    }

    /// <summary>No section boundary is specified exactly by the UX spec beyond its illustrative
    /// example (T-24h / T-1h / Incident) — buckets are: more than 6h before, 1-6h before, under
    /// 1h before, and at-or-after the incident.</summary>
    private static string SectionFor(DateTimeOffset at, DateTimeOffset incidentAt)
    {
        TimeSpan before = incidentAt - at;
        if (before > TimeSpan.FromHours(6))
        {
            return "T-24h";
        }

        if (before > TimeSpan.FromHours(1))
        {
            return "T-6h";
        }

        return before > TimeSpan.Zero ? "T-1h" : "Incident";
    }

    /// <summary>
    /// GOTCHA within a GOTCHA: every replacement entity here (&amp;#58;, &amp;#59;, &amp;#35;)
    /// itself contains a ';' AND a '#'. Three sequential .Replace() calls, in any order,
    /// re-escape whichever entity a later call's own target character happens to appear
    /// inside — ':' -> "&amp;#58;" introduces a ';' that the ';' pass then mangles into
    /// "&amp;#58&amp;#59;", and so on for every ordering. A single regex pass that maps each
    /// character to its entity from the ORIGINAL text avoids this: <see cref="Regex.Replace"/>
    /// with a MatchEvaluator never re-scans the replacement text it just produced.
    /// </summary>
    private static string Escape(string text)
    {
        string s = text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
        s = SpecialCharacter().Replace(s, static m => m.Value switch
        {
            ":" => "&#58;",
            ";" => "&#59;",
            "#" => "&#35;",
            _ => m.Value,
        });

        return s.Length > MaxEntryLength ? s[..MaxEntryLength] + "…" : s;
    }
}
