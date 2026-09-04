namespace Aftermath.Tools;

using System.ComponentModel;
using ModelContextProtocol.Server;
using Aftermath.Cli;
using Aftermath.Configuration;
using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Discovery;
using Aftermath.Rendering;
using Aftermath.Sources;

/// <summary>
/// The MCP front door (Goal G). No AI client is added anywhere in this tool — the host becomes
/// the narrator, calling these five deterministic, offline-by-default tools and writing the
/// prose itself. Every tool is a thin wrapper over the exact code CliRunner's `collect`/`draft`
/// verbs already use (via <see cref="EvidenceGatherer"/>), so the CLI and the MCP surface can
/// never drift onto different behaviour.
/// </summary>
[McpServerToolType]
public sealed class IncidentTools(WorkspaceRegistry registry)
{
    /// <summary>§6, GOTCHA: a tool result is not a document. A silently truncated timeline
    /// reads to a model as "quiet", which is worse than an explicit truncation notice.</summary>
    private const int MaxEvents = 500;

    /// <summary>Put verbatim in incident_timeline's description — the only place the model
    /// reliably reads it (plan.md §6).</summary>
    private const string NarrationContract =
        "Returns evidence, not analysis. Each event carries a Confidence: Observed (read from a "
        + "durable artefact), Inferred (derived by the tool — read its Caveat before relying on "
        + "it), Reported (asserted by a remote system). Do not state that any change caused the "
        + "incident; the tool ranks proximity only, and no service in this estate exposes its "
        + "running version, so a release appearing here is not proof it was live. Cite the "
        + "Provenance string for every claim you make. If a source's status is Skipped or "
        + "Failed, say so in your summary rather than reasoning as though its evidence were "
        + "absent.";

    [McpServerTool(Name = "incident_services")]
    [Description(
        "Builds the service join table from a workspace of git clones: the Octopus project "
        + "slug, deployable package name, OpenTelemetry service name, log-file prefix and every "
        + "distinct log4net conversion pattern found for each repo. Call this first — it tells "
        + "you which services exist and whether each one's production log path is even "
        + "discoverable from the clone (logPrefixIsToken=true means it is not: production logs "
        + "must be copied locally and pointed at with a log root before any log evidence is "
        + "available for that service).")]
    public ToolResult Services(
        [Description("Directory holding the git clones, e.g. c:\\workspace\\work. Defaults to INCIDENTTIMELINE_WORKSPACE.")]
        string? workspace = null)
    {
        (string? ws, ToolResult? failure) = registry.ResolveWorkspace(workspace);
        if (ws is null)
        {
            return failure!;
        }

        try
        {
            IReadOnlyList<ServiceManifest> manifests = WorkspaceScanner.Scan(ws);
            return ToolResult.Ok(manifests, $"{manifests.Count} service manifest(s).");
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
    }

    [McpServerTool(Name = "incident_collect")]
    [Description(
        "Gathers raw evidence around an incident from every offline source and returns one "
        + "SourceResult per source: the git source (release tags, commits on HEAD, "
        + "CHANGELOG.md entries) and the log source (log4net files, Error.txt crash dumps, "
        + "HTTP status/latency recovered from log text) when a log root is supplied. A source "
        + "never throws — Skipped or Failed statuses are returned as data, with the reason and "
        + "what to do about it in Message. Capped at 500 events total; a truncated response "
        + "says so explicitly in the affected source's Message rather than silently returning "
        + "fewer.")]
    public async Task<ToolResult> CollectAsync(
        [Description("ISO-8601 incident instant WITH a zone, e.g. 2026-07-17T13:00:00Z. Never guessed.")]
        string at,
        [Description("Directory holding the git clones. Defaults to INCIDENTTIMELINE_WORKSPACE.")]
        string? workspace = null,
        [Description("How far back to look. Default 24h. Units: s, m, h, d.")]
        string? window = null,
        [Description("How far forward to look. Default 2h.")]
        string? forward = null,
        [Description("Directory holding locally-copied log4net files and Error.txt dumps. Defaults to "
            + "INCIDENTTIMELINE_LOG_ROOT. Without one, log evidence is Skipped, not guessed at.")]
        string? logRoot = null,
        [Description("IANA zone the log files' timestamps are local to. Defaults to INCIDENTTIMELINE_TIMEZONE, "
            + "then Europe/Dublin.")]
        string? timezone = null,
        [Description("JIRA project prefixes to extract from commits/changelogs, comma separated. Default TRAN,PMOB,PMD.")]
        string? ticketPrefixes = null,
        [Description("Opt into the network-touching sources (Octopus deploys, DbExplorer diagnostics, "
            + "GitLab pipelines, GitHub Actions runs) — false by default, so a call with this omitted never "
            + "opens a socket. Each source still individually Skips if its own URL/token is not configured.")]
        bool online = false,
        CancellationToken ct = default)
    {
        (string? ws, ToolResult? failure) = registry.ResolveWorkspace(workspace);
        if (ws is null)
        {
            return failure!;
        }

        try
        {
            (IReadOnlyList<SourceResult> results, _) = await this.GatherAsync(
                ws, at, window, forward, logRoot, timezone, ticketPrefixes, online, ct).ConfigureAwait(false);
            (IReadOnlyList<SourceResult> capped, bool truncated) = CapEvents(results, MaxEvents);
            return ToolResult.Ok(
                capped,
                truncated
                    ? $"{capped.Count} source result(s); TRUNCATED at {MaxEvents} events total — narrow the window to see the rest."
                    : $"{capped.Count} source result(s).");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("INVALID_ARGUMENT", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
    }

    [McpServerTool(Name = "incident_timeline")]
    [Description(NarrationContract)]
    public async Task<ToolResult> TimelineAsync(
        [Description("ISO-8601 incident instant WITH a zone, e.g. 2026-07-17T13:00:00Z. Never guessed.")]
        string at,
        [Description("Directory holding the git clones. Defaults to INCIDENTTIMELINE_WORKSPACE.")]
        string? workspace = null,
        [Description("How far back to look. Default 24h. Units: s, m, h, d.")]
        string? window = null,
        [Description("How far forward to look. Default 2h.")]
        string? forward = null,
        [Description("Directory holding locally-copied log4net files and Error.txt dumps. Defaults to "
            + "INCIDENTTIMELINE_LOG_ROOT. Without one, log evidence is Skipped, not guessed at.")]
        string? logRoot = null,
        [Description("IANA zone the log files' timestamps are local to. Defaults to INCIDENTTIMELINE_TIMEZONE, "
            + "then Europe/Dublin.")]
        string? timezone = null,
        [Description("JIRA project prefixes to extract from commits/changelogs, comma separated. Default TRAN,PMOB,PMD.")]
        string? ticketPrefixes = null,
        [Description("Opt into the network-touching sources (Octopus deploys, DbExplorer diagnostics, "
            + "GitLab pipelines, GitHub Actions runs) — false by default, so a call with this omitted never "
            + "opens a socket. Each source still individually Skips if its own URL/token is not configured.")]
        bool online = false,
        CancellationToken ct = default)
    {
        (string? ws, ToolResult? failure) = registry.ResolveWorkspace(workspace);
        if (ws is null)
        {
            return failure!;
        }

        try
        {
            (IReadOnlyList<SourceResult> results, IncidentWindow incidentWindow) = await this.GatherAsync(
                ws, at, window, forward, logRoot, timezone, ticketPrefixes, online, ct).ConfigureAwait(false);
            Timeline timeline = TimelineBuilder.Build(results, incidentWindow);
            (IReadOnlyList<TimelineEvent> capped, bool truncated) = CapEvents(timeline.Events, MaxEvents);

            var payload = new { events = capped, timeline.Sources, timeline.Window, timeline.TraceGroups };
            return ToolResult.Ok(
                payload,
                truncated
                    ? $"{capped.Count} of {timeline.Events.Count} event(s); TRUNCATED at {MaxEvents} — narrow the window to see the rest."
                    : $"{capped.Count} event(s).");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("INVALID_ARGUMENT", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
    }

    [McpServerTool(Name = "incident_suspects")]
    [Description(
        "Ranks change events (releases, deploys, commits) by proximity to the incident. This "
        + "is coincidence and proximity only — never a cause. Score = proximity x kindWeight x "
        + "blastRadius; every Suspect carries the underlying event (with its own Provenance to "
        + "cite) and Label, which is always the fixed string 'changed nearby'. Do not phrase a "
        + "high-scoring suspect as having caused the incident: no service in this estate exposes "
        + "its running version, so a release appearing here is not proof it was even live.")]
    public async Task<ToolResult> SuspectsAsync(
        [Description("ISO-8601 incident instant WITH a zone, e.g. 2026-07-17T13:00:00Z. Never guessed.")]
        string at,
        [Description("Directory holding the git clones. Defaults to INCIDENTTIMELINE_WORKSPACE.")]
        string? workspace = null,
        [Description("How far back to look. Default 24h. Units: s, m, h, d.")]
        string? window = null,
        [Description("How far forward to look. Default 2h.")]
        string? forward = null,
        [Description("Directory holding locally-copied log4net files and Error.txt dumps. Defaults to "
            + "INCIDENTTIMELINE_LOG_ROOT. Without one, log evidence is Skipped, not guessed at.")]
        string? logRoot = null,
        [Description("IANA zone the log files' timestamps are local to. Defaults to INCIDENTTIMELINE_TIMEZONE, "
            + "then Europe/Dublin.")]
        string? timezone = null,
        [Description("JIRA project prefixes to extract from commits/changelogs, comma separated. Default TRAN,PMOB,PMD.")]
        string? ticketPrefixes = null,
        [Description("Opt into the network-touching sources (Octopus deploys, DbExplorer diagnostics, "
            + "GitLab pipelines, GitHub Actions runs) — false by default, so a call with this omitted never "
            + "opens a socket. Each source still individually Skips if its own URL/token is not configured.")]
        bool online = false,
        CancellationToken ct = default)
    {
        (string? ws, ToolResult? failure) = registry.ResolveWorkspace(workspace);
        if (ws is null)
        {
            return failure!;
        }

        try
        {
            (IReadOnlyList<SourceResult> results, IncidentWindow incidentWindow) = await this.GatherAsync(
                ws, at, window, forward, logRoot, timezone, ticketPrefixes, online, ct).ConfigureAwait(false);
            Timeline timeline = TimelineBuilder.Build(results, incidentWindow);
            IReadOnlyList<Suspect> suspects = SuspectRanker.Rank(timeline);
            return ToolResult.Ok(suspects, $"{suspects.Count} suspect(s), ranked highest first.");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("INVALID_ARGUMENT", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
    }

    [McpServerTool(Name = "incident_draft")]
    [Description(
        "Renders the redacted markdown postmortem draft: what was looked at, what could not be "
        + "seen, what changed nearby, error rate and latency, the full timeline (a mermaid "
        + "diagram, then the event table), and open questions for the reviewer. Every event is "
        + "redacted before it reaches the page, and the whole document is redacted once more as "
        + "a final safety net — there is no way to disable this. Hand this document to the user "
        + "largely as-is; it is written to be pasted into JIRA, Slack or Confluence, not "
        + "rewritten.")]
    public async Task<ToolResult> DraftAsync(
        [Description("ISO-8601 incident instant WITH a zone, e.g. 2026-07-17T13:00:00Z. Never guessed.")]
        string at,
        [Description("Directory holding the git clones. Defaults to INCIDENTTIMELINE_WORKSPACE.")]
        string? workspace = null,
        [Description("How far back to look. Default 24h. Units: s, m, h, d.")]
        string? window = null,
        [Description("How far forward to look. Default 2h.")]
        string? forward = null,
        [Description("Directory holding locally-copied log4net files and Error.txt dumps. Defaults to "
            + "INCIDENTTIMELINE_LOG_ROOT. Without one, log evidence is Skipped, not guessed at.")]
        string? logRoot = null,
        [Description("IANA zone the log files' timestamps are local to. Defaults to INCIDENTTIMELINE_TIMEZONE, "
            + "then Europe/Dublin.")]
        string? timezone = null,
        [Description("JIRA project prefixes to extract from commits/changelogs, comma separated. Default TRAN,PMOB,PMD.")]
        string? ticketPrefixes = null,
        [Description("Opt into the network-touching sources (Octopus deploys, DbExplorer diagnostics, "
            + "GitLab pipelines, GitHub Actions runs) — false by default, so a call with this omitted never "
            + "opens a socket. Each source still individually Skips if its own URL/token is not configured.")]
        bool online = false,
        CancellationToken ct = default)
    {
        (string? ws, ToolResult? failure) = registry.ResolveWorkspace(workspace);
        if (ws is null)
        {
            return failure!;
        }

        try
        {
            (IReadOnlyList<SourceResult> results, IncidentWindow incidentWindow) = await this.GatherAsync(
                ws, at, window, forward, logRoot, timezone, ticketPrefixes, online, ct).ConfigureAwait(false);
            Timeline timeline = TimelineBuilder.Build(results, incidentWindow);

            ITimelineNarrator narrator = new TemplateNarrator(new Redactor());
            string document = await narrator.NarrateAsync(timeline, ct).ConfigureAwait(false);
            return ToolResult.Ok(document, "Draft rendered.");
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail("INVALID_ARGUMENT", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return ToolResult.Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
    }

    private Task<(IReadOnlyList<SourceResult> Results, IncidentWindow Window)> GatherAsync(
        string workspace,
        string at,
        string? window,
        string? forward,
        string? logRoot,
        string? timezone,
        string? ticketPrefixes,
        bool online,
        CancellationToken ct) =>
        EvidenceGatherer.GatherAsync(
            workspace,
            at,
            window,
            forward,
            logRoot ?? registry.LogRoot,
            timezone ?? registry.Timezone,
            TicketKeys.ParsePrefixes(ticketPrefixes),
            ct,
            registry.OnlineDefaults with { Online = online });

    private static (IReadOnlyList<SourceResult> Results, bool Truncated) CapEvents(IReadOnlyList<SourceResult> results, int max)
    {
        int total = results.Sum(r => r.Events.Count);
        if (total <= max)
        {
            return (results, false);
        }

        var capped = new List<SourceResult>(results.Count);
        int remaining = max;
        foreach (SourceResult r in results)
        {
            int take = Math.Max(0, Math.Min(remaining, r.Events.Count));
            bool sourceTruncated = take < r.Events.Count;
            remaining -= take;

            capped.Add(sourceTruncated
                ? r with
                {
                    Events = [.. r.Events.Take(take)],
                    Message = $"{r.Message} TRUNCATED at {max} events total; narrow the window to see the rest.",
                }
                : r);
        }

        return (capped, true);
    }

    private static (IReadOnlyList<TimelineEvent> Events, bool Truncated) CapEvents(IReadOnlyList<TimelineEvent> events, int max) =>
        events.Count <= max ? (events, false) : ([.. events.Take(max)], true);
}
