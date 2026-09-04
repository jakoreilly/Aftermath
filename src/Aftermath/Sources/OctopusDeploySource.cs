namespace Aftermath.Sources;

using System.Net.Http.Json;
using Aftermath.Contracts;

/// <summary>One deployment, already joined to its release version and environment name — the
/// shape <see cref="OctopusDeploySource"/> needs regardless of how a client obtained it.</summary>
public sealed record OctopusDeployment
{
    public required string ReleaseVersion { get; init; }

    public required string EnvironmentName { get; init; }

    public required DateTimeOffset DeployedAtUtc { get; init; }

    public required string State { get; init; }

    public required string DeploymentId { get; init; }
}

/// <summary>
/// The seam that keeps <see cref="OctopusDeploySource"/> unit-testable, mirroring
/// <see cref="IGitRunner"/> (Phase 2's local exemplar for this exact problem): tests supply
/// canned deployments, only <see cref="HttpOctopusClient"/> touches the network.
/// </summary>
public interface IOctopusClient
{
    /// <summary>Null <c>Deployments</c> with a non-null <c>Error</c> means the call failed
    /// (network, auth, or the project was not found) — a value, not an exception, matching
    /// every other source in this tool.</summary>
    Task<(IReadOnlyList<OctopusDeployment>? Deployments, string? Error)> GetDeploymentsAsync(string projectSlug, CancellationToken ct);
}

/// <summary>
/// Deploy evidence from Octopus Deploy, joined on <see cref="ServiceManifest.OctopusProjectSlug"/>.
/// Emits <see cref="EventKind.Deploy"/> at <see cref="Confidence.Reported"/> — an assertion by a
/// remote system this tool cannot re-check, unlike a git tag it can read directly.
///
/// GOTCHA (probed 2026-09-04, not fully verified): <c>deploy.acme.example</c> IS reachable
/// from this machine — the probe returned a genuine Octopus 401 body ("You must be logged in
/// or provide a valid API key…") with no credential supplied, confirming the endpoint is real
/// and live. But no <c>INCIDENTTIMELINE_OCTOPUS_TOKEN</c> was available to this run, so the
/// success-path response shape below was never confirmed against a live authenticated call —
/// it follows Octopus's own public, documented REST API (stable across versions), not a guess
/// from nothing, but treat every field name as needing reconfirmation before trusting this
/// source in anger. This is why the source stays opt-in behind --online regardless of
/// reachability (hard constraint 1) and degrades to Skipped, never Failed, per project.
/// </summary>
public sealed class OctopusDeploySource : IEvidenceSource
{
    public const string SourceName = "octopus";

    private readonly IOctopusClient? client;

    /// <summary>Null when Octopus was not opted into for this run (no --online, or no
    /// INCIDENTTIMELINE_OCTOPUS_URL configured) — the whole source Skips rather than guessing.</summary>
    public OctopusDeploySource(IOctopusClient? client) => this.client = client;

    public string Name => SourceName;

    public bool IsOffline => false;

    public async Task<SourceResult> CollectAsync(IncidentWindow window, IReadOnlyList<ServiceManifest> services, CancellationToken ct)
    {
        if (this.client is null)
        {
            return SourceResult.Skipped(
                SourceName,
                "Octopus is opt-in and not configured for this run — pass --online with "
                + "INCIDENTTIMELINE_OCTOPUS_URL (and INCIDENTTIMELINE_OCTOPUS_TOKEN if the "
                + "space requires one) to query it.");
        }

        var events = new List<TimelineEvent>();
        var noSlug = new List<string>();
        var failed = new List<string>();
        int queried = 0;

        foreach (ServiceManifest service in services)
        {
            ct.ThrowIfCancellationRequested();

            // Phase 1 GOTCHA: a null slug means "deploys cannot be resolved for this
            // service" — a NuGet library like `shared`, not a deployable project.
            if (string.IsNullOrEmpty(service.OctopusProjectSlug))
            {
                noSlug.Add(service.Key);
                continue;
            }

            queried++;
            (IReadOnlyList<OctopusDeployment>? deployments, string? error) =
                await this.client.GetDeploymentsAsync(service.OctopusProjectSlug, ct).ConfigureAwait(false);

            if (deployments is null)
            {
                failed.Add($"{service.Key}: {error}");
                continue;
            }

            events.AddRange(deployments
                .Where(d => window.Contains(d.DeployedAtUtc))
                .Select(d => BuildEvent(service, d)));
        }

        events.Sort(static (a, b) => a.At.CompareTo(b.At));
        return SourceResult.Ok(SourceName, events, BuildMessage(queried, services.Count, noSlug, failed, events.Count));
    }

    private static TimelineEvent BuildEvent(ServiceManifest service, OctopusDeployment d) => new()
    {
        At = d.DeployedAtUtc,
        Kind = EventKind.Deploy,
        Confidence = Confidence.Reported,
        Service = service.Key,
        Summary = $"Deployed {d.ReleaseVersion} to {d.EnvironmentName} ({d.State})",
        Provenance = $"octopus:{service.OctopusProjectSlug}@{d.DeploymentId}",
        Caveat = "Reported by Octopus Deploy — a remote system this tool cannot independently re-check.",
    };

    private static string BuildMessage(int queried, int total, IReadOnlyList<string> noSlug, IReadOnlyList<string> failed, int eventCount)
    {
        string message = $"Queried {queried} of {total} services with an Octopus project; {eventCount} deployment(s) in window.";
        if (noSlug.Count > 0)
        {
            message += " No Octopus project for: " + string.Join(", ", noSlug) + ".";
        }

        if (failed.Count > 0)
        {
            message += " Failed: " + string.Join("; ", failed) + ".";
        }

        return message;
    }
}

/// <summary>
/// Calls Octopus's real REST API directly. Every DTO below matches the public, documented
/// shape (PascalCase properties, <c>{ "Items": [...] }</c> collection envelopes) — see the
/// class-level GOTCHA on <see cref="OctopusDeploySource"/> for what is and is not verified.
/// </summary>
public sealed class HttpOctopusClient : IOctopusClient
{
    private readonly HttpClient http;
    private readonly string spaceId;

    /// <summary><paramref name="http"/> must already carry BaseAddress and, if configured, the
    /// <c>X-Octopus-ApiKey</c> header — set up once by the caller, not by this class, so the
    /// credential never needs to pass through a constructor argument that could be logged.</summary>
    public HttpOctopusClient(HttpClient http, string spaceId = "Spaces-1")
    {
        this.http = http;
        this.spaceId = spaceId;
    }

    public async Task<(IReadOnlyList<OctopusDeployment>? Deployments, string? Error)> GetDeploymentsAsync(string projectSlug, CancellationToken ct)
    {
        try
        {
            OctopusProjectDto? project = await this.GetJsonAsync<OctopusProjectDto>(
                $"/api/{this.spaceId}/projects/{Uri.EscapeDataString(projectSlug)}", ct).ConfigureAwait(false);
            if (project is null)
            {
                return (null, $"project '{projectSlug}' not found or not authorised");
            }

            OctopusItemsDto<OctopusDeploymentDto>? deployments = await this.GetJsonAsync<OctopusItemsDto<OctopusDeploymentDto>>(
                $"/api/{this.spaceId}/deployments?projects={project.Id}&take=100", ct).ConfigureAwait(false);
            OctopusItemsDto<OctopusEnvironmentDto>? environments = await this.GetJsonAsync<OctopusItemsDto<OctopusEnvironmentDto>>(
                $"/api/{this.spaceId}/environments", ct).ConfigureAwait(false);

            var environmentNames = (environments?.Items ?? [])
                .ToDictionary(e => e.Id, e => e.Name, StringComparer.Ordinal);

            var results = new List<OctopusDeployment>();
            foreach (OctopusDeploymentDto d in deployments?.Items ?? [])
            {
                if (d.CompletedTime is not { } completed)
                {
                    continue; // still in progress; nothing to report yet
                }

                OctopusReleaseDto? release = await this.GetJsonAsync<OctopusReleaseDto>(
                    $"/api/{this.spaceId}/releases/{d.ReleaseId}", ct).ConfigureAwait(false);
                if (release is null)
                {
                    continue;
                }

                results.Add(new OctopusDeployment
                {
                    ReleaseVersion = release.Version,
                    EnvironmentName = environmentNames.GetValueOrDefault(d.EnvironmentId, d.EnvironmentId),
                    DeployedAtUtc = completed,
                    State = d.State ?? "Unknown",
                    DeploymentId = d.Id,
                });
            }

            return (results, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, ex.Message);
        }
    }

    private async Task<T?> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await this.http.GetAsync(path, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false)
            : default;
    }
}

internal sealed record OctopusProjectDto(string Id, string Slug, string Name);

internal sealed record OctopusDeploymentDto(string Id, string ReleaseId, string EnvironmentId, string? State, DateTimeOffset? CompletedTime);

internal sealed record OctopusReleaseDto(string Id, string Version);

internal sealed record OctopusEnvironmentDto(string Id, string Name);

internal sealed record OctopusItemsDto<T>(IReadOnlyList<T> Items);
