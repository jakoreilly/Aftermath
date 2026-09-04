namespace Aftermath.Correlation;

using Aftermath.Contracts;
using Aftermath.Logs;
using Aftermath.Sources;

/// <summary>
/// Merges every source's events into one timeline. Pure — no I/O, no clock read — so it is the
/// easiest phase to test exhaustively (§4, Phase 4 header).
/// </summary>
public static class TimelineBuilder
{
    public static Timeline Build(IReadOnlyList<SourceResult> results, IncidentWindow window)
    {
        List<TimelineEvent> windowed = [.. results.SelectMany(r => r.Events).Where(e => window.Contains(e.At))];
        List<TimelineEvent> deduplicated = Deduplicate(windowed);

        // A total order, not just a sort key: two events at the same millisecond must not
        // reorder between runs, or the golden-file test flaps. OrderBy (unlike List.Sort) is a
        // documented STABLE sort, so any remaining tie keeps its original relative position —
        // and that position is itself deterministic because it comes from `results`, whose
        // source order is fixed by CliRunner's registration list.
        List<TimelineEvent> ordered =
        [
            .. deduplicated
                .OrderBy(e => e.At)
                .ThenBy(e => e.Service, StringComparer.Ordinal)
                .ThenBy(e => e.Kind),
        ];

        return new Timeline
        {
            Events = ordered,
            Sources = results,
            Window = window,
            TraceGroups = BuildTraceGroups(ordered),
        };
    }

    /// <summary>
    /// Same Service + Kind + At-to-the-second + normalised Summary is one event reported more
    /// than once — e.g. the same crash appearing in both an on-host log file and a syslog
    /// mirror. Keeps the strongest Confidence and notes how many reports collapsed into it,
    /// rather than silently discarding the weaker copies' corroboration.
    /// </summary>
    private static List<TimelineEvent> Deduplicate(List<TimelineEvent> events)
    {
        var groups = new Dictionary<(string Service, EventKind Kind, DateTimeOffset AtSecond, string Summary), List<TimelineEvent>>();
        var keyOrder = new List<(string, EventKind, DateTimeOffset, string)>();

        foreach (TimelineEvent e in events)
        {
            var key = (e.Service, e.Kind, TruncateToSecond(e.At), LogClusterer.Normalise(e.Summary));
            if (!groups.TryGetValue(key, out List<TimelineEvent>? members))
            {
                members = [];
                groups[key] = members;
                keyOrder.Add(key);
            }

            members.Add(e);
        }

        return [.. keyOrder.Select(key => Merge(groups[key]))];
    }

    private static TimelineEvent Merge(List<TimelineEvent> members)
    {
        if (members.Count == 1)
        {
            return members[0];
        }

        TimelineEvent strongest = members
            .OrderBy(ConfidenceStrength)
            .ThenBy(m => m.Provenance, StringComparer.Ordinal)
            .First();

        int duplicates = members.Count - 1;
        string note = $"(+{duplicates} duplicate report{(duplicates == 1 ? string.Empty : "s")})";
        return strongest with { Caveat = strongest.Caveat is null ? note : $"{strongest.Caveat} {note}" };
    }

    /// <summary>Observed is set by reading a durable artefact directly; Reported is asserted by
    /// a system this tool cannot re-check. Lower is stronger — matches the enum's own
    /// declaration order (Contracts/TimelineEvent.cs), spelled out here rather than relying on
    /// that ordinal implicitly.</summary>
    private static int ConfidenceStrength(TimelineEvent e) => e.Confidence switch
    {
        Confidence.Observed => 0,
        Confidence.Inferred => 1,
        Confidence.Reported => 2,
        _ => 3,
    };

    private static DateTimeOffset TruncateToSecond(DateTimeOffset at) =>
        new(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, at.Offset);

    private static IReadOnlyList<TraceGroup> BuildTraceGroups(IReadOnlyList<TimelineEvent> orderedEvents) =>
        [.. orderedEvents
            .Where(e => !string.IsNullOrEmpty(e.TraceId))
            .GroupBy(e => e.TraceId!, StringComparer.Ordinal)
            .Select(g => new TraceGroup { TraceId = g.Key, Events = [.. g] })];
}
