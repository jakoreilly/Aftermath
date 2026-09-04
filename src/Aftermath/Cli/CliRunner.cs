namespace Aftermath.Cli;

using System.Text.Json;
using System.Text.Json.Serialization;
using Aftermath.Contracts;
using Aftermath.Correlation;
using Aftermath.Discovery;
using Aftermath.Rendering;
using Aftermath.Sources;

/// <summary>
/// Direct command-line entry point for quick, human-typed access to the same logic an MCP
/// host will call in Phase 6 — no protocol framing, one JSON result printed to stdout per
/// call. This is a second front door onto the same code, not a parallel implementation.
/// </summary>
public static class CliRunner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 0 ? 1 : 0;
        }

        string verb = args[0];
        Dictionary<string, string?> flags = ArgParser.Parse(args.Skip(1).ToArray());

        // Ctrl-C must unwind the running git child processes rather than orphan them.
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            return await DispatchAsync(verb, flags, cancellation.Token).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return Fail("INVALID_ARGUMENT", ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return Fail("WORKSPACE_NOT_FOUND", ex.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("CANCELLED", "Cancelled before the collection finished.");
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static Task<int> DispatchAsync(string verb, Dictionary<string, string?> flags, CancellationToken ct) =>
        verb switch
        {
            "services" => Task.FromResult(Services(flags)),
            "collect" => CollectAsync(flags, ct),
            "draft" => DraftAsync(flags, ct),
            _ => Task.FromResult(
                Fail("UNKNOWN_VERB", $"Unknown command '{verb}'. Run with --help for the list.")),
        };

    private static int Services(Dictionary<string, string?> flags)
    {
        IReadOnlyList<ServiceManifest> manifests = WorkspaceScanner.Scan(RequireWorkspace(flags));
        Console.WriteLine(JsonSerializer.Serialize(manifests, Json));
        return 0;
    }

    /// <summary>
    /// Emits an ARRAY of <see cref="SourceResult"/> even though Phase 2 registers one source.
    /// Phases 3 and 7 add more, and a shape that flipped from object to array between phases
    /// would break every consumer written against it.
    /// </summary>
    private static async Task<int> CollectAsync(Dictionary<string, string?> flags, CancellationToken ct)
    {
        (IReadOnlyList<SourceResult> results, _) = await GatherFromFlagsAsync(flags, ct).ConfigureAwait(false);
        Console.WriteLine(JsonSerializer.Serialize(results, Json));
        return results.Any(r => r.Status == SourceStatus.Failed) ? 1 : 0;
    }

    /// <summary>
    /// Renders the fixed-template markdown document (Phase 5): every event redacted before it
    /// reaches the page, with the fully rendered document redacted once more as a final safety
    /// net (constraint 2). --out defaults to stdout when omitted.
    /// </summary>
    private static async Task<int> DraftAsync(Dictionary<string, string?> flags, CancellationToken ct)
    {
        (IReadOnlyList<SourceResult> results, IncidentWindow window) = await GatherFromFlagsAsync(flags, ct).ConfigureAwait(false);
        Timeline timeline = TimelineBuilder.Build(results, window);

        ITimelineNarrator narrator = new TemplateNarrator(new Redactor());
        string document = await narrator.NarrateAsync(timeline, ct).ConfigureAwait(false);

        string? outPath = flags.Get("out");
        if (outPath is null)
        {
            Console.WriteLine(document);
        }
        else
        {
            await File.WriteAllTextAsync(outPath, document, ct).ConfigureAwait(false);
        }

        return results.Any(r => r.Status == SourceStatus.Failed) ? 1 : 0;
    }

    /// <summary>Thin adapter from CLI flags onto the shared <see cref="EvidenceGatherer"/> both
    /// front doors call — see its own doc comment for why this is a single call site.</summary>
    private static Task<(IReadOnlyList<SourceResult> Results, IncidentWindow Window)> GatherFromFlagsAsync(
        Dictionary<string, string?> flags, CancellationToken ct) =>
        EvidenceGatherer.GatherAsync(
            RequireWorkspace(flags),
            flags.Get("at") ?? string.Empty,
            flags.Get("window"),
            flags.Get("forward"),
            flags.Get("log-root"),
            flags.Get("timezone"),
            TicketKeys.ParsePrefixes(flags.Get("ticket-prefixes")),
            ct,
            OnlineOptionsFromFlags(flags));

    /// <summary>Every network-touching source is opt-in behind --online (hard constraint 1);
    /// without it, none of the flags or environment variables below are even read. Each value
    /// resolves supplied flag -> environment variable -> null, matching the exemplar's own
    /// "supplied value -> env var -> throw naming the var" convention (constraint 1 names both
    /// --octopus-token and INCIDENTTIMELINE_*_TOKEN as valid).</summary>
    private static OnlineSourceOptions OnlineOptionsFromFlags(Dictionary<string, string?> flags) => new()
    {
        Online = flags.GetBool("online"),
        OctopusUrl = FlagOrVar(flags, "octopus-url", OnlineSourceOptions.OctopusUrlVar),
        OctopusToken = FlagOrVar(flags, "octopus-token", OnlineSourceOptions.OctopusTokenVar),
        DbExplorerUrl = FlagOrVar(flags, "dbexplorer-url", OnlineSourceOptions.DbExplorerUrlVar),
        DbExplorerToken = FlagOrVar(flags, "dbexplorer-token", OnlineSourceOptions.DbExplorerTokenVar),
        GitLabUrl = FlagOrVar(flags, "gitlab-url", OnlineSourceOptions.GitLabUrlVar),
        GitLabToken = FlagOrVar(flags, "gitlab-token", OnlineSourceOptions.GitLabTokenVar),
    };

    private static string? FlagOrVar(Dictionary<string, string?> flags, string flagName, string envVar) =>
        flags.Get(flagName) ?? Environment.GetEnvironmentVariable(envVar);

    private static string RequireWorkspace(Dictionary<string, string?> flags) =>
        flags.Get("workspace")
        ?? throw new ArgumentException(
            "--workspace is required: the directory holding your clones, e.g. c:\\workspace\\work");

    private static int Fail(string code, string message)
    {
        Console.Error.WriteLine($"{code}: {message}");
        return 1;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Aftermath — assembles a provenance-labelled incident timeline.
            Read-only and offline by default: it opens no database and needs no credential.

            Usage:
              Aftermath services --workspace <dir>
              Aftermath collect  --workspace <dir> --at <instant> [--window 24h] [--forward 2h]
              Aftermath draft    --workspace <dir> --at <instant> [--out draft.md]

            Commands:
              services   Build the service join table from a workspace of git clones.
                         Reports, per repo: the Octopus project slug, deployable package
                         name, OpenTelemetry service name, log-file prefix and every
                         distinct log4net conversion pattern.

              collect    Gather evidence around an incident from every offline source and
                         print one JSON result per source: the git source (release tags,
                         commits on HEAD, CHANGELOG.md entries) and the log source (log4net
                         files, Error.txt crash dumps, HTTP status/latency recovered from
                         log text) when --log-root is supplied.

              draft      Render the fixed-template postmortem draft: what was looked at,
                         what changed nearby (ranked, never asserted as a cause), error rate
                         and latency, and the full timeline. Every event is redacted before
                         it reaches the page — there is no flag to disable this. Writes to
                         --out, or stdout when omitted.

            Flags:
              --workspace <dir>          Directory holding the clones (e.g. c:\workspace\work).
              --at <instant>             ISO-8601 incident time WITH a zone, e.g.
                                         2026-07-17T13:00:00Z. Never guessed.
              --window <duration>        How far back to look. Default 24h. Units: s, m, h, d.
              --forward <duration>       How far forward to look. Default 2h.
              --ticket-prefixes <list>   JIRA project prefixes to extract, comma separated.
                                         Default TRAN,PMOB,PMD.
              --log-root <dir>           Directory holding locally-copied log4net files and
                                         Error.txt dumps. Production's own log path is an
                                         Octopus token and can never be discovered from a
                                         clone, so without this flag log evidence is Skipped,
                                         not guessed at.
              --timezone <iana-id>       IANA zone the log files' timestamps are local to.
                                         Default Europe/Dublin. Every log4net timestamp in the
                                         estate is host-local with no offset recorded.
              --out <path>               (draft only) File to write the document to. Prints to
                                         stdout when omitted.
              --online                   Opt into the network-touching sources (Octopus
                                         deploys, DbExplorer diagnostics, GitLab pipelines).
                                         Absent by default, so the default run completes with
                                         the network cable unplugged. Each source still
                                         individually Skips if its own URL/token below is not
                                         configured.
              --octopus-url / --octopus-token           Octopus Deploy space base URL and API key.
              --dbexplorer-url / --dbexplorer-token     DbExplorer base URL and bearer token
                                                         (needs the Profiler scope).
              --gitlab-url / --gitlab-token              GitLab base URL and personal access token.

            Environment:
              INCIDENTTIMELINE_GIT_PATH        Path to the git executable when it is not on PATH.
              INCIDENTTIMELINE_OCTOPUS_URL/_TOKEN, INCIDENTTIMELINE_DBEXPLORER_URL/_TOKEN,
              INCIDENTTIMELINE_GITLAB_URL/_TOKEN     Fallbacks for the --online flags above,
                                                      read when the matching flag is omitted.
            """);
    }
}
