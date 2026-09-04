namespace Aftermath.Cli;

using Aftermath.Contracts;
using Aftermath.Discovery;
using Aftermath.Logs;
using Aftermath.Sources;

/// <summary>
/// The one place both front doors — the CLI's `collect`/`draft` verbs and Phase 6's MCP
/// tools — build an <see cref="IncidentWindow"/> and run every registered source. A single
/// call site so the two front doors can never drift onto different source lists or window
/// logic (CliRunner's own doc comment: "a second front door onto the same code, not a
/// parallel implementation").
/// </summary>
public static class EvidenceGatherer
{
    public static async Task<(IReadOnlyList<SourceResult> Results, IncidentWindow Window)> GatherAsync(
        string workspace,
        string at,
        string? window,
        string? forward,
        string? logRoot,
        string? timezone,
        IReadOnlyList<string>? ticketPrefixes,
        CancellationToken ct,
        OnlineSourceOptions? online = null)
    {
        DateTimeOffset atUtc = TimeArgs.ParseInstant("at", at);
        IncidentWindow defaults = IncidentWindow.Default(atUtc);
        var incidentWindow = new IncidentWindow
        {
            AtUtc = atUtc,
            LookBack = TimeArgs.ParseDuration("window", window, defaults.LookBack),
            LookForward = TimeArgs.ParseDuration("forward", forward, defaults.LookForward),
        };

        IReadOnlyList<ServiceManifest> services = WorkspaceScanner.Scan(workspace);
        List<IEvidenceSource> sources =
        [
            new GitReleaseSource(new ProcessGitRunner(), ticketPrefixes is { Count: > 0 } ? ticketPrefixes : TicketKeys.DefaultPrefixes),
            new LogFileSource(logRoot, ResolveZone(timezone)),
        ];

        sources.AddRange(BuildOnlineSources(online ?? OnlineSourceOptions.Offline));

        var results = new List<SourceResult>(sources.Count);
        foreach (IEvidenceSource source in sources)
        {
            results.Add(await source.CollectAsync(incidentWindow, services, ct).ConfigureAwait(false));
        }

        return (results, incidentWindow);
    }

    /// <summary>
    /// Every source here is opt-in behind --online (hard constraint 1) — with it absent, this
    /// returns nothing, and `draft`'s output is unchanged from before Phase 7 existed. With it
    /// present, each source still individually decides whether IT has enough configuration to
    /// try, so partial configuration degrades per source rather than all-or-nothing.
    /// </summary>
    private static IEnumerable<IEvidenceSource> BuildOnlineSources(OnlineSourceOptions online)
    {
        if (!online.Online)
        {
            yield break;
        }

        yield return new OctopusDeploySource(BuildOctopusClient(online));
        yield return new DbDiagnosticsSource(BuildDbExplorerClient(online));
        yield return new GitLabPipelineSource(new ProcessGitRunner(), BuildGitLabClient(online));
        yield return new GitHubActionsSource(new ProcessGitRunner(), BuildGitHubClient(online));
    }

    private static IOctopusClient? BuildOctopusClient(OnlineSourceOptions online)
    {
        if (string.IsNullOrWhiteSpace(online.OctopusUrl))
        {
            return null;
        }

        var http = new HttpClient { BaseAddress = new Uri(online.OctopusUrl) };
        if (!string.IsNullOrWhiteSpace(online.OctopusToken))
        {
            http.DefaultRequestHeaders.Add("X-Octopus-ApiKey", online.OctopusToken);
        }

        return new HttpOctopusClient(http);
    }

    private static IDbExplorerClient? BuildDbExplorerClient(OnlineSourceOptions online)
    {
        if (string.IsNullOrWhiteSpace(online.DbExplorerUrl))
        {
            return null;
        }

        var http = new HttpClient { BaseAddress = new Uri(online.DbExplorerUrl) };
        if (!string.IsNullOrWhiteSpace(online.DbExplorerToken))
        {
            // Confirmed invariant (exemplar cache): auth is Authorization: Bearer dbx_… — the
            // repo's own docs say X-Api-Token / X-DbExplorer-Token, and both are wrong.
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", online.DbExplorerToken);
        }

        return new HttpDbExplorerClient(http);
    }

    private static IGitLabClient? BuildGitLabClient(OnlineSourceOptions online)
    {
        if (string.IsNullOrWhiteSpace(online.GitLabUrl))
        {
            return null;
        }

        var http = new HttpClient { BaseAddress = new Uri(online.GitLabUrl) };
        if (!string.IsNullOrWhiteSpace(online.GitLabToken))
        {
            http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", online.GitLabToken);
        }

        return new HttpGitLabClient(http);
    }

    /// <summary>Unlike the other three, GitHub keys off the TOKEN, not the URL: the base URL
    /// defaults to api.github.com and only a GitHub Enterprise Server host needs to override
    /// it. No token -> null -> the source Skips.</summary>
    private static IGitHubClient? BuildGitHubClient(OnlineSourceOptions online)
    {
        if (string.IsNullOrWhiteSpace(online.GitHubToken))
        {
            return null;
        }

        var http = new HttpClient
        {
            BaseAddress = new Uri(string.IsNullOrWhiteSpace(online.GitHubUrl) ? OnlineSourceOptions.DefaultGitHubUrl : online.GitHubUrl),
        };
        http.DefaultRequestHeaders.Add("User-Agent", "Aftermath");
        http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", online.GitHubToken);
        return new HttpGitHubClient(http);
    }

    /// <summary>--timezone defaults to Europe/Dublin (constraint 11: resolved via ICU, never
    /// with InvariantGlobalization set) but must not be hardcoded — not every host this tool
    /// is pointed at is in Ireland.</summary>
    public static TimeZoneInfo ResolveZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LogFileClock.DefaultZone;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(value);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new ArgumentException($"timezone '{value}' is not a known IANA time zone id.", ex);
        }
    }
}
