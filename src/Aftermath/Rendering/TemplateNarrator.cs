namespace Aftermath.Rendering;

using System.Globalization;
using System.Text;
using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Sources;

/// <summary>
/// Fills the fixed UX-spec template (plan.md §5.2) with no model, no network and no
/// nondeterminism (constraint 6: nothing renders before this phase). Every event is redacted
/// before it reaches the page, and the fully assembled document is redacted once more as the
/// final safety net at the single output boundary (constraint 2).
/// </summary>
public sealed class TemplateNarrator : ITimelineNarrator
{
    private readonly Redactor redactor;
    private readonly TimeProvider clock;
    private readonly string version;

    public TemplateNarrator(Redactor redactor, TimeProvider? clock = null, string? version = null)
    {
        this.redactor = redactor;
        this.clock = clock ?? TimeProvider.System;
        this.version = version ?? typeof(TemplateNarrator).Assembly.GetName().Version?.ToString(3) ?? "dev";
    }

    public Task<string> NarrateAsync(Timeline timeline, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(this.Render(timeline));
    }

    private string Render(Timeline timeline)
    {
        IReadOnlyList<TimelineEvent> events = [.. timeline.Events.Select(this.redactor.RedactEvent)];
        var sb = new StringBuilder();

        this.AppendHeader(sb, timeline);
        AppendWhatWeLookedAt(sb, timeline.Sources);
        AppendWhatWeCouldNotSee(sb, timeline.Sources);
        AppendChangedNearby(sb, timeline, events);
        AppendErrorRateAndLatency(sb, events);
        AppendTimeline(sb, timeline, events);
        AppendOpenQuestions(sb, events);

        // Final safety net at the single output boundary — every field above was already
        // redacted individually, but nothing leaves this method without passing through here.
        return this.redactor.Apply(sb.ToString());
    }

    private void AppendHeader(StringBuilder sb, Timeline timeline)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Incident draft — {timeline.Window.AtUtc:yyyy-MM-dd HH:mm} UTC");
        sb.AppendLine();
        DateTimeOffset now = this.clock.GetUtcNow();
        sb.AppendLine(CultureInfo.InvariantCulture, $"> Assembled by Aftermath {this.version} at {now:yyyy-MM-dd HH:mm} UTC.");
        sb.AppendLine("> **This is a draft built from evidence, not an analysis.** Nothing below is a");
        sb.AppendLine("> statement of cause. Times are UTC. Every line cites where it came from.");
        sb.AppendLine();
    }

    private static void AppendWhatWeLookedAt(StringBuilder sb, IReadOnlyList<SourceResult> sources)
    {
        sb.AppendLine("## What we looked at");
        sb.AppendLine();
        sb.AppendLine("| Source | Status | Detail |");
        sb.AppendLine("|---|---|---|");
        foreach (SourceResult source in sources)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {source.SourceName} | {StatusBadge(source.Status)} | {source.Message} |");
        }

        sb.AppendLine();
    }

    private static void AppendWhatWeCouldNotSee(StringBuilder sb, IReadOnlyList<SourceResult> sources)
    {
        sb.AppendLine("## What we could not see");
        sb.AppendLine();

        SourceResult[] gaps = [.. sources.Where(s => s.Status is SourceStatus.Skipped or SourceStatus.Failed)];
        if (gaps.Length == 0)
        {
            sb.AppendLine("- None — every registered source completed.");
        }
        else
        {
            foreach (SourceResult gap in gaps)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **{gap.SourceName}** ({StatusBadge(gap.Status)}): {gap.Message}");
            }
        }

        sb.AppendLine();
    }

    private static void AppendChangedNearby(StringBuilder sb, Timeline timeline, IReadOnlyList<TimelineEvent> redactedEvents)
    {
        sb.AppendLine("## Changed nearby");
        sb.AppendLine();

        Timeline redactedTimeline = timeline with { Events = redactedEvents };
        IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(redactedTimeline);

        if (suspects.Count == 0)
        {
            sb.AppendLine(
                "Nothing changed in this window. Given a median gap of roughly two to four "
                + "weeks between releases, that is the normal case — look outside the change axis.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("| When (UTC) | Service | What changed | Tickets | Proximity | Evidence |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (Suspect suspect in suspects)
        {
            TimelineEvent e = suspect.Event;
            string tickets = e.Tickets.Count == 0 ? "—" : string.Join(", ", e.Tickets);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {e.At:yyyy-MM-dd HH:mm} | {e.Service} | {ConfidenceMarker(e.Confidence)}{e.Summary} | {tickets} "
                + $"| {FormatProximity(e.At, timeline.Window.AtUtc)} | {e.Provenance} |");
        }

        sb.AppendLine();
    }

    private static void AppendErrorRateAndLatency(StringBuilder sb, IReadOnlyList<TimelineEvent> events)
    {
        sb.AppendLine("## Error rate and latency");
        sb.AppendLine();
        sb.AppendLine("Derived from `HTTP Response:` log lines. Blank cells mean the line format for that service");
        sb.AppendLine("is not parseable by this tool — not that traffic was zero.");
        sb.AppendLine();
        sb.AppendLine("| Minute (UTC) | Service | Requests | 4xx | 5xx | p95 ms |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (TimelineEvent e in events.Where(e => e.Kind == EventKind.HttpMetrics))
        {
            if (HttpMetricRow.TryParse(e, out HttpMetricRow row))
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| {e.At:yyyy-MM-dd HH:mm} | {e.Service} | {row.Requests} | {row.FourXx} | {row.FiveXx} | {row.P95Ms} |");
            }
            else
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"| — | {e.Service} | | | | |");
            }
        }

        sb.AppendLine();
    }

    /// <summary>The mermaid block, then the full event table — exactly that order (§5.2).</summary>
    private static void AppendTimeline(StringBuilder sb, Timeline timeline, IReadOnlyList<TimelineEvent> events)
    {
        sb.AppendLine("## Timeline");
        sb.AppendLine();

        if (events.Count == 0)
        {
            sb.AppendLine(
                "No evidence found in the window. Widen it with --window, or check "
                + "that --workspace points at your clones and --log-root at a copied logs directory.");
            sb.AppendLine();
            return;
        }

        sb.AppendLine("```mermaid");
        sb.Append(MermaidTimeline.Render(events, timeline.Window.AtUtc));
        sb.AppendLine("```");
        sb.AppendLine();

        var footnotes = new List<string>();
        sb.AppendLine("| At (UTC) | Kind | Service | Summary | Tickets | Evidence |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (TimelineEvent e in events)
        {
            string marker = ConfidenceMarker(e.Confidence);
            if (marker.Length > 0 && e.Caveat is not null)
            {
                footnotes.Add($"{marker}{footnotes.Count + 1}: {e.Caveat}");
            }

            string tickets = e.Tickets.Count == 0 ? "—" : string.Join(", ", e.Tickets);
            string markerRef = marker.Length > 0 && e.Caveat is not null ? $"{marker}{footnotes.Count}" : marker;
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {e.At:yyyy-MM-dd HH:mm:ss} | {e.Kind} | {e.Service} | {markerRef}{e.Summary} | {tickets} | {e.Provenance} |");
        }

        if (footnotes.Count > 0)
        {
            sb.AppendLine();
            foreach (string note in footnotes)
            {
                sb.AppendLine(note);
            }
        }

        sb.AppendLine();
    }

    private static void AppendOpenQuestions(StringBuilder sb, IReadOnlyList<TimelineEvent> events)
    {
        sb.AppendLine("## Open questions for the reviewer");
        sb.AppendLine();
        sb.AppendLine(
            "- Which of the changes above was actually live in the affected environment at the time?");
        sb.AppendLine("  This tool cannot tell you: no service exposes its running version.");
        sb.AppendLine(
            "- Was the log level in this environment high enough to record what you are looking for?");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Observed level: {ObservedLevel(events)}.");
        sb.AppendLine(
            "- Were there errors that never reached the central collector? Syslog delivery is UDP.");
        sb.AppendLine();
    }

    /// <summary>
    /// This tool only ever keeps WARN-and-above events (§3.7 clustering discards the rest), so
    /// it cannot see whether the environment's configured `Logging.Level` (an Octopus
    /// variable, §3.4) was set below that anyway. Answering honestly from what was actually
    /// collected, rather than fabricating a precision the tool does not have: if any
    /// LogWarning-kind event exists, WARN was at least reaching the file; if only Error/Fatal
    /// exist, WARN may have been suppressed in this environment; if none exist at all, nothing
    /// can be said.
    /// </summary>
    private static string ObservedLevel(IReadOnlyList<TimelineEvent> events)
    {
        bool anyWarning = events.Any(e => e.Kind == EventKind.LogWarning);
        bool anyErrorOrFatal = events.Any(e => e.Kind is EventKind.LogError or EventKind.LogFatal);

        if (anyWarning)
        {
            return "WARN (seen in the logs read)";
        }

        if (anyErrorOrFatal)
        {
            return "ERROR or above only — WARN may be suppressed in this environment; check the "
                + "appender's Logging.Level";
        }

        return "not determined — no log evidence was collected for this window";
    }

    private static string FormatProximity(DateTimeOffset at, DateTimeOffset incidentAt)
    {
        TimeSpan delta = incidentAt - at;
        if (delta <= TimeSpan.Zero)
        {
            return FormatSpan(delta.Negate()) + " after";
        }

        return FormatSpan(delta) + " before";
    }

    private static string FormatSpan(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";

    private static string StatusBadge(SourceStatus status) => status switch
    {
        SourceStatus.Ok => "✅",
        SourceStatus.Skipped => "⚠️",
        SourceStatus.Failed => "❌",
        _ => "?",
    };

    private static string ConfidenceMarker(Confidence confidence) => confidence switch
    {
        Confidence.Inferred => "~",
        Confidence.Reported => "†",
        _ => string.Empty,
    };
}
