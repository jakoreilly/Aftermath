namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Logs;

public sealed class LogClustererTests
{
    private static RawLogLine Line(
        DateTimeOffset at,
        string message,
        EventKind kind = EventKind.LogError,
        Confidence confidence = Confidence.Observed,
        string? traceId = null,
        string provenance = "svc.log:1") =>
        new()
        {
            At = at,
            Kind = kind,
            Confidence = confidence,
            Message = message,
            Provenance = provenance,
            TraceId = traceId,
        };

    [Theory]
    [InlineData("Timeout for request 3f9a1c2b-8d4e-4a11-9c3d-7e2f6b1a0d55", "Timeout for request <guid>")]
    [InlineData("Batch of 12345 records failed", "Batch of <n> records failed")]
    [InlineData("Started at 2026-09-03T14:22:10.123Z", "Started at <timestamp>")]
    [InlineData("Payload \"account-42\" rejected", "Payload <string> rejected")]
    [InlineData("Retry 7 of 3", "Retry 7 of 3")] // short digit runs (<3 digits) are left alone
    public void Normalise_replaces_variable_content_with_placeholders(string message, string expected)
    {
        Assert.Equal(expected, LogClusterer.Normalise(message));
    }

    [Fact]
    public void Identical_messages_after_normalisation_collapse_into_one_cluster_with_a_count()
    {
        var lines = new List<RawLogLine>
        {
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "Batch of 100 records failed"),
            Line(new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero), "Batch of 250 records failed"),
            Line(new DateTimeOffset(2026, 9, 3, 10, 2, 0, TimeSpan.Zero), "Batch of 999 records failed"),
        };

        IReadOnlyList<TimelineEvent> events = LogClusterer.Cluster("svc", lines);

        TimelineEvent cluster = Assert.Single(events, e => e.Detail!.Contains("count=3"));
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), cluster.At); // first-seen
        Assert.Contains("×3 occurrences", cluster.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_messages_stay_in_separate_clusters()
    {
        var lines = new List<RawLogLine>
        {
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "Disk full"),
            Line(new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero), "Connection refused"),
        };

        IReadOnlyList<TimelineEvent> events = LogClusterer.Cluster("svc", lines);

        Assert.Equal(2, events.Count(e => e.Detail is not null && e.Detail.StartsWith("first=", StringComparison.Ordinal)));
    }

    [Fact]
    public void Cluster_confidence_is_inferred_if_any_member_was_inferred()
    {
        var lines = new List<RawLogLine>
        {
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "same message", confidence: Confidence.Observed),
            Line(new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero), "same message", confidence: Confidence.Inferred),
        };

        TimelineEvent cluster = LogClusterer.Cluster("svc", lines).Single(e => e.Detail!.Contains("count=2"));

        Assert.Equal(Confidence.Inferred, cluster.Confidence);
    }

    [Fact]
    public void Distinct_trace_ids_at_error_or_above_each_get_their_own_event()
    {
        var lines = new List<RawLogLine>
        {
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "step one failed", traceId: "trace-a"),
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 5, TimeSpan.Zero), "step two failed", traceId: "trace-a"),
            Line(new DateTimeOffset(2026, 9, 3, 10, 1, 0, TimeSpan.Zero), "unrelated failure", traceId: "trace-b"),
        };

        IReadOnlyList<TimelineEvent> events = LogClusterer.Cluster("svc", lines);

        TimelineEvent traceA = Assert.Single(events, e => e.TraceId == "trace-a" && e.Summary.Contains("error-or-above"));
        Assert.Contains("2 error-or-above", traceA.Summary, StringComparison.Ordinal);
        Assert.Contains(events, e => e.TraceId == "trace-b" && e.Summary.Contains("error-or-above"));
    }

    [Fact]
    public void Warning_level_lines_are_excluded_from_trace_id_grouping()
    {
        var lines = new List<RawLogLine>
        {
            Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "just a warning", kind: EventKind.LogWarning, traceId: "trace-c"),
        };

        IReadOnlyList<TimelineEvent> events = LogClusterer.Cluster("svc", lines);

        Assert.DoesNotContain(events, e => e.TraceId == "trace-c" && e.Summary.Contains("error-or-above"));
    }

    [Fact]
    public void Lines_with_no_trace_id_produce_no_trace_grouping_event()
    {
        var lines = new List<RawLogLine> { Line(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero), "no trace here") };

        IReadOnlyList<TimelineEvent> events = LogClusterer.Cluster("svc", lines);

        Assert.DoesNotContain(events, e => e.Summary.Contains("error-or-above"));
    }
}
