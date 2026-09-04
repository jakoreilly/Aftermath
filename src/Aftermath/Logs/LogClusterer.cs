namespace Aftermath.Logs;

using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Deterministic error-line clustering — no AI (§3.7, explicit). Raw error lines are noise: a
/// batch failure that logs the same stack trace 500 times should read as one event with a
/// count, not 500 near-identical ones. Normalises each message by replacing every GUID,
/// integer run of 3+ digits, ISO timestamp and quoted string with a placeholder, then groups by
/// the normalised form. Separately emits one event per distinct trace_id seen at Error or
/// above — a cross-cutting view, since one request can carry several distinct error messages
/// across the same trace.
/// </summary>
public static partial class LogClusterer
{
    [GeneratedRegex(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(
        @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(?:[.,]\d+)?(?:Z|[+-]\d{2}:?\d{2})?",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex IsoTimestampPattern();

    [GeneratedRegex(@"""[^""]*""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex QuotedStringPattern();

    [GeneratedRegex(@"\d{3,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LongDigitRunPattern();

    /// <summary>Replaces every GUID, ISO timestamp, quoted string and 3+-digit run with a
    /// placeholder, in that order — a timestamp or GUID would otherwise also be swallowed by
    /// the digit-run pass, which must run last so it only catches what is left over
    /// (request/account ids, byte counts, and the like).</summary>
    public static string Normalise(string message)
    {
        string s = GuidPattern().Replace(message, "<guid>");
        s = IsoTimestampPattern().Replace(s, "<timestamp>");
        s = QuotedStringPattern().Replace(s, "<string>");
        s = LongDigitRunPattern().Replace(s, "<n>");
        return s;
    }

    public static IReadOnlyList<TimelineEvent> Cluster(string serviceKey, IReadOnlyList<RawLogLine> lines)
    {
        var events = new List<TimelineEvent>();
        events.AddRange(ClusterByMessage(serviceKey, lines));
        events.AddRange(ClusterByTraceId(serviceKey, lines));
        return events;
    }

    private static IEnumerable<TimelineEvent> ClusterByMessage(string serviceKey, IReadOnlyList<RawLogLine> lines)
    {
        var groups = new Dictionary<string, List<RawLogLine>>(StringComparer.Ordinal);
        var keyOrder = new List<string>();

        foreach (RawLogLine line in lines)
        {
            string key = Normalise(line.Message);
            if (!groups.TryGetValue(key, out List<RawLogLine>? members))
            {
                members = [];
                groups[key] = members;
                keyOrder.Add(key);
            }

            members.Add(line);
        }

        return keyOrder.Select(key => BuildMessageClusterEvent(serviceKey, groups[key]));
    }

    private static TimelineEvent BuildMessageClusterEvent(string serviceKey, IReadOnlyList<RawLogLine> members)
    {
        RawLogLine first = members[0];
        RawLogLine last = members[^1];
        EventKind kind = WorstKind(members);

        return new TimelineEvent
        {
            At = first.At,
            Kind = kind,
            Confidence = members.Any(m => m.Confidence == Confidence.Inferred) ? Confidence.Inferred : Confidence.Observed,
            Service = serviceKey,
            Summary = members.Count == 1 ? first.Message : $"{first.Message} (×{members.Count} occurrences)",
            Provenance = first.Provenance,
            Caveat = first.Caveat,
            TraceId = first.TraceId,
            SpanId = first.SpanId,
            CorrelationPrefix = first.CorrelationPrefix,
            Detail = $"first={first.At:O} last={last.At:O} count={members.Count}",
        };
    }

    private static IEnumerable<TimelineEvent> ClusterByTraceId(string serviceKey, IReadOnlyList<RawLogLine> lines)
    {
        IEnumerable<IGrouping<string, RawLogLine>> byTrace = lines
            .Where(l => l.TraceId is { Length: > 0 } && l.Kind is EventKind.LogError or EventKind.LogFatal)
            .GroupBy(l => l.TraceId!, StringComparer.Ordinal);

        foreach (IGrouping<string, RawLogLine> group in byTrace)
        {
            RawLogLine[] members = [.. group.OrderBy(m => m.At)];
            RawLogLine first = members[0];
            string[] distinctMessages = [.. members.Select(m => m.Message).Distinct().Take(3)];

            yield return new TimelineEvent
            {
                At = first.At,
                Kind = WorstKind(members),
                Confidence = members.Any(m => m.Confidence == Confidence.Inferred) ? Confidence.Inferred : Confidence.Observed,
                Service = serviceKey,
                Summary = $"{members.Length} error-or-above log line(s) share trace {group.Key}",
                Provenance = first.Provenance,
                TraceId = group.Key,
                Detail = string.Join("; ", distinctMessages),
            };
        }
    }

    private static EventKind WorstKind(IReadOnlyList<RawLogLine> members) =>
        members.Any(m => m.Kind == EventKind.LogFatal) ? EventKind.LogFatal
        : members.Any(m => m.Kind == EventKind.LogError) ? EventKind.LogError
        : EventKind.LogWarning;
}
