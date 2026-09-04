namespace Aftermath.Logs;

using System.Globalization;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Recovers HTTP status and latency from SharedHttpLoggingMiddleware's fixed log line, with no
/// metrics store and no network (plan.md §3.6a, verbatim template at
/// shared-master/.../SharedHttpLoggingMiddleware.cs:128).
/// </summary>
public static partial class HttpMetrics
{
    [GeneratedRegex(
        @"^HTTP Response: (?<method>\S+) (?<path>\S+) \| StatusCode: (?<status>\d+) \|.*\| ElapsedMs: (?<elapsed>\d+)\s*$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ResponseLine();

    public static bool TryParse(string message, out int statusCode, out int elapsedMs)
    {
        Match m = ResponseLine().Match(message);
        if (m.Success &&
            int.TryParse(m.Groups["status"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out statusCode) &&
            int.TryParse(m.Groups["elapsed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out elapsedMs))
        {
            return true;
        }

        statusCode = 0;
        elapsedMs = 0;
        return false;
    }

    /// <summary>
    /// accountservice and billing-service use RequestLoggingMiddleware from the
    /// Acme.Ledger.Shared NuGet package instead — its source is not in the workspace,
    /// so its line format is unknown and no metric can be extracted. Reported explicitly rather
    /// than as a numeric zero: a zero that means "not parsed" reads exactly like a zero that
    /// means "outage" (§3.6a).
    /// </summary>
    public static TimelineEvent Unavailable(string serviceKey, DateTimeOffset atUtc) => new()
    {
        At = atUtc,
        Kind = EventKind.HttpMetrics,
        Confidence = Confidence.Inferred,
        Service = serviceKey,
        Summary = "HTTP metrics unavailable for this service",
        Provenance = $"{serviceKey}: RequestLoggingMiddleware (Acme.Ledger.Shared) — source not in workspace",
        Caveat = "This service does not use SharedHttpLoggingMiddleware, so HTTP status and "
            + "latency cannot be recovered from its logs. Absence of a metric here is not "
            + "evidence the service had no traffic.",
    };
}

/// <summary>Buckets `HTTP Response:` samples per UTC minute and turns each minute into one
/// TimelineEvent — request count, 4xx count, 5xx count, p50/p95 ElapsedMs (§3.6a).</summary>
public sealed class HttpMetricsAccumulator
{
    private readonly SortedDictionary<DateTimeOffset, List<(int Status, int ElapsedMs)>> buckets = new();

    public void Add(DateTimeOffset at, string message)
    {
        if (!HttpMetrics.TryParse(message, out int status, out int elapsedMs))
        {
            return;
        }

        var minute = new DateTimeOffset(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0, TimeSpan.Zero);
        if (!this.buckets.TryGetValue(minute, out List<(int, int)>? samples))
        {
            samples = [];
            this.buckets[minute] = samples;
        }

        samples.Add((status, elapsedMs));
    }

    public IReadOnlyList<TimelineEvent> BuildEvents(string serviceKey) =>
        this.buckets.Select(kv => BuildEvent(serviceKey, kv.Key, kv.Value)).ToArray();

    private static TimelineEvent BuildEvent(string serviceKey, DateTimeOffset minute, List<(int Status, int ElapsedMs)> samples)
    {
        int fourXx = samples.Count(s => s.Status is >= 400 and < 500);
        int fiveXx = samples.Count(s => s.Status >= 500);
        int[] elapsed = [.. samples.Select(s => s.ElapsedMs).Order()];
        int p50 = Percentile(elapsed, 0.50);
        int p95 = Percentile(elapsed, 0.95);

        return new TimelineEvent
        {
            At = minute,
            Kind = EventKind.HttpMetrics,
            Confidence = Confidence.Observed,
            Service = serviceKey,
            Summary = $"{samples.Count} request(s), {fourXx} 4xx, {fiveXx} 5xx, p50 {p50}ms, p95 {p95}ms",
            Provenance = $"{serviceKey} HTTP Response lines, minute {minute:yyyy-MM-ddTHH:mm}Z",
            Detail = $"requests={samples.Count} 4xx={fourXx} 5xx={fiveXx} p50Ms={p50} p95Ms={p95}",
        };
    }

    /// <summary>Nearest-rank percentile over an already-sorted list.</summary>
    private static int Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        int rank = (int)Math.Ceiling(percentile * sortedValues.Count) - 1;
        return sortedValues[Math.Clamp(rank, 0, sortedValues.Count - 1)];
    }
}
