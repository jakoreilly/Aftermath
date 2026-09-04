namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Sources;

public sealed class TimelineBuilderTests
{
    private static readonly IncidentWindow Window = new()
    {
        AtUtc = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero),
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    private static TimelineEvent Ev(
        DateTimeOffset at,
        string service,
        EventKind kind = EventKind.Commit,
        string summary = "something happened",
        Confidence confidence = Confidence.Observed,
        string? traceId = null,
        string? provenance = null) =>
        new()
        {
            At = at,
            Kind = kind,
            Confidence = confidence,
            Service = service,
            Summary = summary,
            Provenance = provenance ?? $"{service}@{at:O}",
            TraceId = traceId,
        };

    private static SourceResult Ok(string name, params TimelineEvent[] events) =>
        SourceResult.Ok(name, events, "test");

    /// <summary>A representative mix: two events at the exact same instant on different
    /// services, two events on the same service+kind+second that should de-duplicate, one
    /// event outside the window, and one carrying a trace_id shared with another.</summary>
    private static IReadOnlyList<SourceResult> RepresentativeInput()
    {
        DateTimeOffset t0 = Window.AtUtc.AddHours(-1);
        return
        [
            Ok(
                "git",
                Ev(t0, "core-service", EventKind.Release, "Released v1.15.0"),
                Ev(t0, "billing-service", EventKind.Commit, "fix: retry"),
                Ev(t0.AddSeconds(0.2), "core-service", EventKind.LogError, "NullReferenceException", Confidence.Inferred, provenance: "syslog-copy"),
                Ev(t0, "core-service", EventKind.LogError, "NullReferenceException", Confidence.Observed, provenance: "on-host-log:42"),
                Ev(Window.AtUtc.AddDays(-5), "core-service", EventKind.Commit, "outside the window"),
                Ev(t0.AddMinutes(3), "accountservice", EventKind.LogError, "timeout", traceId: "trace-1"),
                Ev(t0.AddMinutes(4), "billing-service", EventKind.LogError, "downstream timeout", traceId: "trace-1")),
        ];
    }

    private static string Fingerprint(Timeline timeline) => string.Join(
        "\n",
        timeline.Events.Select(e => $"{e.At:O}|{e.Service}|{e.Kind}|{e.Summary}|{e.Confidence}|{e.Caveat}"));

    [Fact]
    public void Total_order_is_stable_across_100_shuffles_of_the_same_input()
    {
        IReadOnlyList<SourceResult> canonical = RepresentativeInput();
        string reference = Fingerprint(TimelineBuilder.Build(canonical, Window));

        var random = new Random(20260904);
        for (int i = 0; i < 100; i++)
        {
            IReadOnlyList<SourceResult> shuffled = ShuffleEvents(canonical, random);
            string actual = Fingerprint(TimelineBuilder.Build(shuffled, Window));
            Assert.Equal(reference, actual);
        }
    }

    private static IReadOnlyList<SourceResult> ShuffleEvents(IReadOnlyList<SourceResult> results, Random random) =>
        [.. results.Select(r => SourceResult.Ok(r.SourceName, [.. r.Events.OrderBy(_ => random.Next())], r.Message))];

    [Fact]
    public void Events_outside_the_window_are_excluded()
    {
        Timeline timeline = TimelineBuilder.Build(RepresentativeInput(), Window);

        Assert.DoesNotContain(timeline.Events, e => e.Summary == "outside the window");
    }

    [Fact]
    public void Duplicate_reports_of_the_same_event_collapse_to_the_strongest_confidence()
    {
        Timeline timeline = TimelineBuilder.Build(RepresentativeInput(), Window);

        TimelineEvent[] nullRefs = [.. timeline.Events.Where(e => e.Summary == "NullReferenceException")];
        TimelineEvent merged = Assert.Single(nullRefs);
        Assert.Equal(Confidence.Observed, merged.Confidence);
        Assert.Equal("on-host-log:42", merged.Provenance);
        Assert.Contains("+1 duplicate report", merged.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void Distinct_events_at_the_same_instant_are_both_kept()
    {
        Timeline timeline = TimelineBuilder.Build(RepresentativeInput(), Window);

        Assert.Contains(timeline.Events, e => e is { Service: "core-service", Kind: EventKind.Release });
        Assert.Contains(timeline.Events, e => e is { Service: "billing-service", Kind: EventKind.Commit });
    }

    [Fact]
    public void Order_ties_break_by_service_then_kind()
    {
        Timeline timeline = TimelineBuilder.Build(RepresentativeInput(), Window);

        int releaseIndex = timeline.Events.ToList().FindIndex(e => e is { Service: "core-service", Kind: EventKind.Release });
        int commitIndex = timeline.Events.ToList().FindIndex(e => e is { Service: "billing-service", Kind: EventKind.Commit });

        // Same instant: "billing-service" sorts before "core-service" ordinally.
        Assert.True(commitIndex < releaseIndex);
    }

    [Fact]
    public void Trace_groups_collect_events_sharing_a_trace_id()
    {
        Timeline timeline = TimelineBuilder.Build(RepresentativeInput(), Window);

        TraceGroup group = Assert.Single(timeline.TraceGroups, g => g.TraceId == "trace-1");
        Assert.Equal(2, group.Events.Count);
        Assert.Contains(group.Events, e => e.Service == "accountservice");
        Assert.Contains(group.Events, e => e.Service == "billing-service");
    }

    [Fact]
    public void Sources_are_carried_through_unchanged()
    {
        IReadOnlyList<SourceResult> input = [Ok("git"), SourceResult.Skipped("logs", "no --log-root")];

        Timeline timeline = TimelineBuilder.Build(input, Window);

        Assert.Equal(2, timeline.Sources.Count);
        Assert.Contains(timeline.Sources, s => s is { SourceName: "logs", Status: SourceStatus.Skipped });
    }
}
