namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Rendering;
using Aftermath.Sources;

public sealed class TemplateNarratorTests
{
    private static readonly IncidentWindow Window = new()
    {
        AtUtc = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero),
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    private static readonly string[] RequiredHeadings =
    [
        "## What we looked at",
        "## What we could not see",
        "## Changed nearby",
        "## Error rate and latency",
        "## Timeline",
        "## Open questions for the reviewer",
    ];

    private static TimelineEvent Ev(
        DateTimeOffset at, string service, EventKind kind, string summary, string? correlation = null, string? detail = null) =>
        new()
        {
            At = at,
            Kind = kind,
            Confidence = Confidence.Observed,
            Service = service,
            Summary = summary,
            Provenance = $"{service}@{at:O}",
            CorrelationPrefix = correlation,
            Detail = detail,
        };

    private static Timeline BuildTimeline(IReadOnlyList<SourceResult> sources) => TimelineBuilder.Build(sources, Window);

    private static TemplateNarrator Narrator() => new(new Redactor(), TimeProvider.System, "test-version");

    [Fact]
    public async Task Document_contains_all_six_headings()
    {
        Timeline timeline = BuildTimeline(
        [
            SourceResult.Ok("git", [Ev(Window.AtUtc.AddHours(-1), "core-service", EventKind.Release, "Released v1.15.0")], "ok"),
        ]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.All(RequiredHeadings, heading => Assert.Contains(heading, document, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_timeline_shows_the_exact_no_evidence_copy()
    {
        Timeline timeline = BuildTimeline([SourceResult.Ok("git", [], "ok")]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains(
            "No evidence found in the window. Widen it with --window, or check "
            + "that --workspace points at your clones and --log-root at a copied logs directory.",
            document,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_changes_shows_the_exact_nothing_changed_copy()
    {
        Timeline timeline = BuildTimeline(
        [
            SourceResult.Ok("logs", [Ev(Window.AtUtc.AddMinutes(-5), "core-service", EventKind.LogError, "boom")], "ok"),
        ]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains(
            "Nothing changed in this window. Given a median gap of roughly two to four "
            + "weeks between releases, that is the normal case — look outside the change axis.",
            document,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skipped_source_appears_under_what_we_could_not_see()
    {
        Timeline timeline = BuildTimeline(
        [
            SourceResult.Ok("git", [], "ok"),
            SourceResult.Skipped("logs", "no --log-root supplied; log evidence not collected."),
        ]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains("no --log-root supplied; log evidence not collected.", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_gaps_shows_the_none_line()
    {
        Timeline timeline = BuildTimeline([SourceResult.Ok("git", [], "ok")]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains("None — every registered source completed.", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_event_is_redacted_before_rendering()
    {
        Timeline timeline = BuildTimeline(
        [
            SourceResult.Ok(
                "logs",
                [Ev(Window.AtUtc.AddMinutes(-5), "core-service", EventKind.LogError, "boom", correlation: "eyJhbGciOiJIUzI1NiJ9.abcdef")],
                "ok"),
        ]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.abcdef", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http_metrics_render_in_the_error_rate_table()
    {
        TimelineEvent metric = Ev(
            Window.AtUtc.AddMinutes(-5), "core-service", EventKind.HttpMetrics, "142 request(s), 3 4xx, 1 5xx, p50 100ms, p95 812ms",
            detail: "requests=142 4xx=3 5xx=1 p50Ms=100 p95Ms=812");
        Timeline timeline = BuildTimeline([SourceResult.Ok("logs", [metric], "ok")]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains("| 142 | 3 | 1 | 812 |", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unavailable_http_metrics_render_with_blank_cells_not_zero()
    {
        TimelineEvent unavailable = Ev(
            Window.AtUtc, "accountservice", EventKind.HttpMetrics, "HTTP metrics unavailable for this service");
        Timeline timeline = BuildTimeline([SourceResult.Ok("logs", [unavailable], "ok")]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains("| — | accountservice | | | | |", document, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_and_timestamp_appear_in_the_header()
    {
        Timeline timeline = BuildTimeline([SourceResult.Ok("git", [], "ok")]);

        string document = await Narrator().NarrateAsync(timeline, CancellationToken.None);

        Assert.Contains("Assembled by Aftermath test-version at", document, StringComparison.Ordinal);
        Assert.Contains("This is a draft built from evidence, not an analysis.", document, StringComparison.Ordinal);
    }
}
