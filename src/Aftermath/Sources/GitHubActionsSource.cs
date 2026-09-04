namespace Aftermath.Sources;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aftermath.Contracts;

public sealed record GitHubRun
{
    public required long Id { get; init; }

    public required string WorkflowName { get; init; }

    public required string HeadSha { get; init; }

    public required string HeadBranch { get; init; }

    public required string Status { get; init; }

    /// <summary>Null while a run is still in progress — only <c>"failure"</c> becomes an event.</summary>
    public required string? Conclusion { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string HtmlUrl { get; init; }
}

/// <summary>The seam that keeps <see cref="GitHubActionsSource"/> unit-testable, mirroring
/// <see cref="IGitLabClient"/>/<see cref="IOctopusClient"/>/<see cref="IGitRunner"/>.</summary>
public interface IGitHubClient
{
    Task<(IReadOnlyList<GitHubRun>? Runs, string? Error)> GetWorkflowRunsAsync(
        string repoSlug, DateTimeOffset createdAfterUtc, DateTimeOffset createdBeforeUtc, CancellationToken ct);
}

/// <summary>
/// CI workflow pass/fail from GitHub Actions, joined by reading each clone's own <c>origin</c>
/// remote (via <see cref="IGitRunner"/> — the same seam <see cref="GitReleaseSource"/> uses)
/// rather than guessing an <c>owner/repo</c>. Emits <see cref="EventKind.CiPipeline"/>,
/// failures only, at <see cref="Confidence.Reported"/> — a workflow result is not a deploy
/// record. The structural twin of <see cref="GitLabPipelineSource"/>: same join, same
/// per-service gap handling, same event shape.
///
/// NOTE (documented, NOT probed): unlike <see cref="GitLabPipelineSource"/>, whose DTOs were
/// captured from a live <c>bull.acme.example</c> response, the DTOs below are built from
/// GitHub's published REST v3 shape for <c>GET /repos/{owner}/{repo}/actions/runs</c>
/// (<c>workflow_runs[]</c> with <c>head_sha</c>, <c>head_branch</c>, <c>conclusion</c>,
/// <c>html_url</c>, <c>updated_at</c>). They have not been re-confirmed against api.github.com
/// on this machine. The tests still run fully offline (constraint 6), against a hand-written
/// copy of that documented shape.
/// </summary>
public sealed class GitHubActionsSource : IEvidenceSource
{
    public const string SourceName = "github";

    private readonly IGitRunner git;
    private readonly IGitHubClient? client;

    /// <summary>Null when GitHub was not opted into for this run.</summary>
    public GitHubActionsSource(IGitRunner git, IGitHubClient? client)
    {
        this.git = git;
        this.client = client;
    }

    public string Name => SourceName;

    public bool IsOffline => false;

    public async Task<SourceResult> CollectAsync(IncidentWindow window, IReadOnlyList<ServiceManifest> services, CancellationToken ct)
    {
        if (this.client is null)
        {
            return SourceResult.Skipped(
                SourceName,
                "GitHub is opt-in and not configured for this run — pass --online with "
                + "INCIDENTTIMELINE_GITHUB_TOKEN (and INCIDENTTIMELINE_GITHUB_URL for GitHub "
                + "Enterprise Server; it defaults to https://api.github.com) to query it.");
        }

        var events = new List<TimelineEvent>();
        var noRemote = new List<string>();
        var failed = new List<string>();

        foreach (ServiceManifest service in services)
        {
            ct.ThrowIfCancellationRequested();

            string? repoSlug = await this.ResolveRepoSlugAsync(service, ct).ConfigureAwait(false);
            if (repoSlug is null)
            {
                noRemote.Add(service.Key);
                continue;
            }

            (IReadOnlyList<GitHubRun>? runs, string? error) =
                await this.client.GetWorkflowRunsAsync(repoSlug, window.FromUtc, window.ToUtc, ct).ConfigureAwait(false);
            if (runs is null)
            {
                failed.Add($"{service.Key}: {error}");
                continue;
            }

            events.AddRange(runs
                .Where(r => window.Contains(r.UpdatedAtUtc) && string.Equals(r.Conclusion, "failure", StringComparison.OrdinalIgnoreCase))
                .Select(r => BuildEvent(service, repoSlug, r)));
        }

        events.Sort(static (a, b) => a.At.CompareTo(b.At));
        return SourceResult.Ok(SourceName, events, BuildMessage(services.Count, noRemote, failed, events.Count));
    }

    /// <summary>Not a working copy, no `origin` remote, or an `origin` that is not a GitHub
    /// URL — a per-service gap, matching <see cref="GitReleaseSource"/>'s own treatment of the
    /// estate's archive-only clones.</summary>
    private async Task<string?> ResolveRepoSlugAsync(ServiceManifest service, CancellationToken ct)
    {
        GitResult result = await this.git.RunAsync(["-C", service.RepoPath, "remote", "get-url", "origin"], ct).ConfigureAwait(false);
        return result.Ok ? GitHubRemote.TryExtractSlug(result.StdOut.Trim()) : null;
    }

    private static TimelineEvent BuildEvent(ServiceManifest service, string repoSlug, GitHubRun r) => new()
    {
        At = r.UpdatedAtUtc,
        Kind = EventKind.CiPipeline,
        Confidence = Confidence.Reported,
        Service = service.Key,
        Summary = $"CI run '{r.WorkflowName}' failed on {r.HeadBranch} ({r.HeadSha[..Math.Min(7, r.HeadSha.Length)]})",
        Provenance = r.HtmlUrl,
        Caveat = "Reported by GitHub Actions — a remote system this tool cannot independently re-check. "
            + "A workflow result is a CI pass/fail, not evidence the change was ever deployed.",
        Detail = $"{repoSlug}#{r.Id}",
    };

    private static string BuildMessage(int total, IReadOnlyList<string> noRemote, IReadOnlyList<string> failed, int eventCount)
    {
        string message = $"{eventCount} failed workflow run(s) in window, across {total} service(s).";
        if (noRemote.Count > 0)
        {
            message += " No resolvable GitHub remote for: " + string.Join(", ", noRemote) + ".";
        }

        if (failed.Count > 0)
        {
            message += " Failed: " + string.Join("; ", failed) + ".";
        }

        return message;
    }
}

/// <summary>Calls GitHub's documented REST v3 API — <c>GET /repos/{owner}/{repo}/actions/runs</c>
/// (see the class-level NOTE on <see cref="GitHubActionsSource"/>).</summary>
public sealed class HttpGitHubClient : IGitHubClient
{
    private readonly HttpClient http;

    /// <summary><paramref name="http"/> must already carry BaseAddress, a <c>User-Agent</c>,
    /// the <c>Accept: application/vnd.github+json</c> header and, if configured, an
    /// <c>Authorization: Bearer</c> header — set up once by the caller.</summary>
    public HttpGitHubClient(HttpClient http) => this.http = http;

    public async Task<(IReadOnlyList<GitHubRun>? Runs, string? Error)> GetWorkflowRunsAsync(
        string repoSlug, DateTimeOffset createdAfterUtc, DateTimeOffset createdBeforeUtc, CancellationToken ct)
    {
        try
        {
            string path = $"/repos/{repoSlug}/actions/runs"
                + $"?created={Uri.EscapeDataString($"{createdAfterUtc:O}..{createdBeforeUtc:O}")}&per_page=100";
            GitHubRunsDto? dto = await this.GetJsonAsync<GitHubRunsDto>(path, ct).ConfigureAwait(false);
            if (dto is null)
            {
                return (null, $"repo '{repoSlug}' not found or not authorised");
            }

            IReadOnlyList<GitHubRun> runs =
            [
                .. dto.WorkflowRuns.Select(d => new GitHubRun
                {
                    Id = d.Id,
                    WorkflowName = d.Name,
                    HeadSha = d.HeadSha,
                    HeadBranch = d.HeadBranch,
                    Status = d.Status,
                    Conclusion = d.Conclusion,
                    UpdatedAtUtc = d.UpdatedAt,
                    HtmlUrl = d.HtmlUrl,
                }),
            ];
            return (runs, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, ex.Message);
        }
    }

    // GitHub's fields are a mix: "id"/"name"/"status"/"conclusion" match these DTOs
    // case-insensitively; the snake_case ones carry explicit [JsonPropertyName] below.
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await this.http.GetAsync(path, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false)
            : default;
    }
}

// GitHub's Actions API is snake_case ("head_sha", "head_branch", "html_url", "updated_at",
// "workflow_runs"). Explicit [JsonPropertyName] here, rather than a global naming policy, so
// the mapping is visible at each field instead of implied by a setting elsewhere.
internal sealed record GitHubRunsDto(
    [property: JsonPropertyName("workflow_runs")] IReadOnlyList<GitHubRunDto> WorkflowRuns);

internal sealed record GitHubRunDto(
    long Id,
    string Name,
    [property: JsonPropertyName("head_sha")] string HeadSha,
    [property: JsonPropertyName("head_branch")] string HeadBranch,
    string Status,
    string? Conclusion,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("html_url")] string HtmlUrl);
