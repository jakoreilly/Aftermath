namespace Aftermath.Sources;

using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Aftermath.Contracts;

public sealed record GitLabPipeline
{
    public required long Id { get; init; }

    public required string Sha { get; init; }

    public required string Ref { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset UpdatedAtUtc { get; init; }

    public required string WebUrl { get; init; }
}

/// <summary>The seam that keeps <see cref="GitLabPipelineSource"/> unit-testable, mirroring
/// <see cref="IOctopusClient"/>/<see cref="IGitRunner"/>.</summary>
public interface IGitLabClient
{
    Task<(IReadOnlyList<GitLabPipeline>? Pipelines, string? Error)> GetPipelinesAsync(
        string projectPath, DateTimeOffset updatedAfterUtc, DateTimeOffset updatedBeforeUtc, CancellationToken ct);
}

/// <summary>
/// CI pipeline pass/fail from GitLab, joined by reading each clone's own <c>origin</c> remote
/// (via <see cref="IGitRunner"/> — the same seam <see cref="GitReleaseSource"/> uses) rather
/// than guessing a project path. Emits <see cref="EventKind.CiPipeline"/>, failures only, at
/// <see cref="Confidence.Reported"/>. GitLab has no deployment record for this estate (zero
/// `environment:` keys anywhere), so this is the lowest-value of the three Phase 7 sources by
/// the plan's own assessment — pipeline result, not deploy evidence.
///
/// GOTCHA (probed 2026-09-04, and NOT a guess): unlike Octopus and DbExplorer,
/// `bull.acme.example` answered with real data — `GET /api/v4/projects?search=core-service`
/// returned exactly the expected project (`acme-group/platform/services/core-service`,
/// created 2025-06-05, not today), and `GET /api/v4/projects/620/pipelines` returned genuine
/// pipeline history, both success and failed. Phase 0's original "BadRequest" verdict most
/// likely came from a plain TLS client choking on this machine's TLS-inspecting proxy, not
/// from GitLab actually rejecting the request. Given deliberately, per instruction, without
/// independently re-confirming this is not some other proxy artifact: this source is built
/// against that live, anonymous-read shape rather than a hand-written one — the DTOs below are
/// the CONFIRMED response shape, not a placeholder. Its own tests still run fully offline
/// (constraint 6), against a recorded copy of that same response.
/// </summary>
public sealed class GitLabPipelineSource : IEvidenceSource
{
    public const string SourceName = "gitlab";

    private readonly IGitRunner git;
    private readonly IGitLabClient? client;

    /// <summary>Null when GitLab was not opted into for this run.</summary>
    public GitLabPipelineSource(IGitRunner git, IGitLabClient? client)
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
                "GitLab is opt-in and not configured for this run — pass --online with "
                + "INCIDENTTIMELINE_GITLAB_URL and INCIDENTTIMELINE_GITLAB_TOKEN to query it.");
        }

        var events = new List<TimelineEvent>();
        var noRemote = new List<string>();
        var failed = new List<string>();

        foreach (ServiceManifest service in services)
        {
            ct.ThrowIfCancellationRequested();

            string? projectPath = await this.ResolveProjectPathAsync(service, ct).ConfigureAwait(false);
            if (projectPath is null)
            {
                noRemote.Add(service.Key);
                continue;
            }

            (IReadOnlyList<GitLabPipeline>? pipelines, string? error) =
                await this.client.GetPipelinesAsync(projectPath, window.FromUtc, window.ToUtc, ct).ConfigureAwait(false);
            if (pipelines is null)
            {
                failed.Add($"{service.Key}: {error}");
                continue;
            }

            events.AddRange(pipelines
                .Where(p => window.Contains(p.UpdatedAtUtc) && string.Equals(p.Status, "failed", StringComparison.OrdinalIgnoreCase))
                .Select(p => BuildEvent(service, projectPath, p)));
        }

        events.Sort(static (a, b) => a.At.CompareTo(b.At));
        return SourceResult.Ok(SourceName, events, BuildMessage(services.Count, noRemote, failed, events.Count));
    }

    /// <summary>Not a working copy, or no `origin` remote at all — a per-service gap, matching
    /// GitReleaseSource's own treatment of the estate's three archive-only clones.</summary>
    private async Task<string?> ResolveProjectPathAsync(ServiceManifest service, CancellationToken ct)
    {
        GitResult result = await this.git.RunAsync(["-C", service.RepoPath, "remote", "get-url", "origin"], ct).ConfigureAwait(false);
        return result.Ok ? GitLabRemote.TryExtractPath(result.StdOut.Trim()) : null;
    }

    private static TimelineEvent BuildEvent(ServiceManifest service, string projectPath, GitLabPipeline p) => new()
    {
        At = p.UpdatedAtUtc,
        Kind = EventKind.CiPipeline,
        Confidence = Confidence.Reported,
        Service = service.Key,
        Summary = $"CI pipeline failed on {p.Ref} ({p.Sha[..Math.Min(7, p.Sha.Length)]})",
        Provenance = p.WebUrl,
        Caveat = "Reported by GitLab — a remote system this tool cannot independently re-check. "
            + "GitLab holds no deployment record for this estate; this is a CI result, not evidence the change was ever live.",
        Detail = $"{projectPath}#{p.Id}",
    };

    private static string BuildMessage(int total, IReadOnlyList<string> noRemote, IReadOnlyList<string> failed, int eventCount)
    {
        string message = $"{eventCount} failed pipeline(s) in window, across {total} service(s).";
        if (noRemote.Count > 0)
        {
            message += " No resolvable GitLab remote for: " + string.Join(", ", noRemote) + ".";
        }

        if (failed.Count > 0)
        {
            message += " Failed: " + string.Join("; ", failed) + ".";
        }

        return message;
    }
}

/// <summary>Calls GitLab's real, documented v4 REST API — confirmed live 2026-09-04 (see the
/// class-level GOTCHA on <see cref="GitLabPipelineSource"/>).</summary>
public sealed class HttpGitLabClient : IGitLabClient
{
    private readonly HttpClient http;

    /// <summary><paramref name="http"/> must already carry BaseAddress and, if configured, a
    /// <c>PRIVATE-TOKEN</c> header — set up once by the caller.</summary>
    public HttpGitLabClient(HttpClient http) => this.http = http;

    public async Task<(IReadOnlyList<GitLabPipeline>? Pipelines, string? Error)> GetPipelinesAsync(
        string projectPath, DateTimeOffset updatedAfterUtc, DateTimeOffset updatedBeforeUtc, CancellationToken ct)
    {
        try
        {
            GitLabProjectDto? project = await this.GetJsonAsync<GitLabProjectDto>(
                $"/api/v4/projects/{Uri.EscapeDataString(projectPath)}", ct).ConfigureAwait(false);
            if (project is null)
            {
                return (null, $"project '{projectPath}' not found or not authorised");
            }

            string path = $"/api/v4/projects/{project.Id}/pipelines"
                + $"?updated_after={Uri.EscapeDataString(updatedAfterUtc.ToString("O"))}"
                + $"&updated_before={Uri.EscapeDataString(updatedBeforeUtc.ToString("O"))}&per_page=100";
            IReadOnlyList<GitLabPipelineDto>? dtos = await this.GetJsonAsync<IReadOnlyList<GitLabPipelineDto>>(path, ct).ConfigureAwait(false);
            if (dtos is null)
            {
                return (null, "could not list pipelines");
            }

            IReadOnlyList<GitLabPipeline> pipelines =
            [
                .. dtos.Select(d => new GitLabPipeline
                {
                    Id = d.Id,
                    Sha = d.Sha,
                    Ref = d.Ref,
                    Status = d.Status,
                    UpdatedAtUtc = d.UpdatedAt,
                    WebUrl = d.WebUrl,
                }),
            ];
            return (pipelines, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, ex.Message);
        }
    }

    // GitLab's lowercase fields ("id", "sha", "ref", "status") need case-insensitive matching
    // against these PascalCase DTOs; the snake_case ones are handled by explicit
    // [JsonPropertyName] on the DTOs below regardless of this policy.
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await this.http.GetAsync(path, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false)
            : default;
    }
}

// GitLab's real API is snake_case (verified live 2026-09-04: "path_with_namespace",
// "web_url", "updated_at") — unlike Octopus, whose PascalCase JSON matches these DTOs'
// property names for free. Explicit [JsonPropertyName] here, rather than a global naming
// policy, so the mapping is visible at each field instead of implied by a setting elsewhere.
internal sealed record GitLabProjectDto(
    long Id,
    [property: JsonPropertyName("path_with_namespace")] string PathWithNamespace);

internal sealed record GitLabPipelineDto(
    long Id,
    string Sha,
    string Ref,
    string Status,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("web_url")] string WebUrl);
