namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Rendering;

public sealed class MermaidTimelineTests
{
    private static readonly DateTimeOffset IncidentAt = new(2026, 7, 17, 13, 0, 0, TimeSpan.Zero);

    private static TimelineEvent Ev(DateTimeOffset at, string service, string summary) => new()
    {
        At = at,
        Kind = EventKind.LogError,
        Confidence = Confidence.Observed,
        Service = service,
        Summary = summary,
        Provenance = "x",
    };

    [Fact]
    public void Every_section_and_entry_line_is_indented_and_non_empty()
    {
        TimelineEvent[] events =
        [
            Ev(IncidentAt.AddHours(-20), "core-service", "SqlException: timeout; connection #12 lost\nsecond line"),
            Ev(IncidentAt.AddMinutes(-5), "billing-service", "boom"),
        ];

        string block = MermaidTimeline.Render(events, IncidentAt);
        string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // First two lines are the fixed "timeline"/"title" header, unindented per mermaid
        // syntax; every line after that is a section or entry line.
        foreach (string line in lines.Skip(2))
        {
            Assert.Matches(@"^\s{4,8}\S", line.TrimEnd('\r'));
        }
    }

    [Fact]
    public void Colons_semicolons_and_hashes_are_escaped()
    {
        TimelineEvent[] events = [Ev(IncidentAt.AddMinutes(-5), "svc", "SqlException: timeout; retry #3")];

        string block = MermaidTimeline.Render(events, IncidentAt);

        Assert.DoesNotContain("SqlException: timeout; retry #3", block, StringComparison.Ordinal);
        Assert.Contains("&#58;", block, StringComparison.Ordinal);
        Assert.Contains("&#59;", block, StringComparison.Ordinal);
        Assert.Contains("&#35;", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Embedded_newlines_do_not_produce_extra_lines()
    {
        TimelineEvent[] events = [Ev(IncidentAt.AddMinutes(-5), "svc", "line one\nline two\r\nline three")];

        string block = MermaidTimeline.Render(events, IncidentAt);
        string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // "timeline" + "title" + "section" + exactly one entry line — a surviving embedded
        // newline would split the entry across two lines instead.
        Assert.Equal(4, lines.Length);
        string entryLine = lines.Single(l => l.Contains("line one", StringComparison.Ordinal));
        Assert.Contains("line one line two line three", entryLine, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_entries_are_capped_at_80_characters_with_an_ellipsis()
    {
        string longSummary = new string('a', 200);
        TimelineEvent[] events = [Ev(IncidentAt.AddMinutes(-5), "svc", longSummary)];

        string block = MermaidTimeline.Render(events, IncidentAt);
        string entryLine = block.Split('\n').Single(l => l.Contains('a'));

        Assert.EndsWith("…", entryLine.TrimEnd('\r'), StringComparison.Ordinal);
    }

    [Fact]
    public void Events_are_grouped_into_sections_by_distance_from_the_incident()
    {
        TimelineEvent[] events =
        [
            Ev(IncidentAt.AddHours(-20), "svc", "old"),
            Ev(IncidentAt.AddMinutes(-30), "svc", "recent"),
            Ev(IncidentAt, "svc", "at incident"),
        ];

        string block = MermaidTimeline.Render(events, IncidentAt);

        Assert.Contains("section T-24h", block, StringComparison.Ordinal);
        Assert.Contains("section T-1h", block, StringComparison.Ordinal);
        Assert.Contains("section Incident", block, StringComparison.Ordinal);
    }
}
