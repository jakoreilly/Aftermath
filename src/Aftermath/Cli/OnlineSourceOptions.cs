namespace Aftermath.Cli;

/// <summary>
/// Credentials for the opt-in network sources (Phase 7: Octopus, DbExplorer, GitLab; plus
/// GitHub Actions). Every field is optional and no secret is required by default (hard
/// constraint 1) — <see cref="Online"/> alone enables nothing; each source individually Skips
/// when its own URL/token is not configured, so partial configuration (e.g. Octopus only, no
/// GitLab) degrades per source rather than all-or-nothing.
/// </summary>
public sealed record OnlineSourceOptions
{
    public const string OctopusUrlVar = "INCIDENTTIMELINE_OCTOPUS_URL";
    public const string OctopusTokenVar = "INCIDENTTIMELINE_OCTOPUS_TOKEN";
    public const string DbExplorerUrlVar = "INCIDENTTIMELINE_DBEXPLORER_URL";
    public const string DbExplorerTokenVar = "INCIDENTTIMELINE_DBEXPLORER_TOKEN";
    public const string GitLabUrlVar = "INCIDENTTIMELINE_GITLAB_URL";
    public const string GitLabTokenVar = "INCIDENTTIMELINE_GITLAB_TOKEN";
    public const string GitHubUrlVar = "INCIDENTTIMELINE_GITHUB_URL";
    public const string GitHubTokenVar = "INCIDENTTIMELINE_GITHUB_TOKEN";

    /// <summary>GitHub's REST API base — the <see cref="GitHubUrl"/> default when only a token
    /// is supplied. GitHub Enterprise Server installs override it with their own host.</summary>
    public const string DefaultGitHubUrl = "https://api.github.com";

    /// <summary>False by default — every network-touching source is opt-in behind this flag,
    /// so the default run completes with the network cable unplugged.</summary>
    public bool Online { get; init; }

    public string? OctopusUrl { get; init; }

    public string? OctopusToken { get; init; }

    public string? DbExplorerUrl { get; init; }

    public string? DbExplorerToken { get; init; }

    public string? GitLabUrl { get; init; }

    public string? GitLabToken { get; init; }

    public string? GitHubUrl { get; init; }

    public string? GitHubToken { get; init; }

    public static readonly OnlineSourceOptions Offline = new();
}
