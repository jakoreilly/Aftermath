namespace Aftermath.Sources;

using System.Net;
using System.Net.Http.Json;
using Aftermath.Contracts;

/// <summary>One blocking or deadlock observation. Estate-wide, not per-service — DbExplorer's
/// diagnostics are a database-server view, and no <see cref="ServiceManifest"/> field names
/// which services share which database.</summary>
public sealed record DbDiagnosticRecord
{
    public required DateTimeOffset AtUtc { get; init; }

    public required string Kind { get; init; }

    public required string Detail { get; init; }
}

/// <summary>The seam that keeps <see cref="DbDiagnosticsSource"/> unit-testable, mirroring
/// <see cref="IOctopusClient"/>/<see cref="IGitRunner"/>.</summary>
public interface IDbExplorerClient
{
    /// <summary><c>Forbidden = true</c> is the documented degradation path (a Reader-scoped
    /// token 403s on <c>/api/diagnostics/*</c>, which needs the Profiler policy) and is
    /// distinct from any other failure: the caller Skips with a specific, actionable message
    /// rather than a generic error.</summary>
    Task<(IReadOnlyList<DbDiagnosticRecord>? Records, string? Error, bool Forbidden)> GetDiagnosticsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <summary>
/// Blocking/deadlock evidence for the incident window, from DbExplorer's
/// <c>/api/diagnostics/*</c> endpoints. Emits <see cref="EventKind.DbBlocking"/> /
/// <see cref="EventKind.DbDeadlock"/> at <see cref="Confidence.Reported"/>.
///
/// GOTCHA (unverified — more so than Octopus): unlike Octopus, this endpoint could not be
/// probed at all. `bull.acme.example/acme-group/platform/dbexplorer` in the exemplar's own
/// cache (`InternalToolExemplar/.claude/plan-doc-exemplars.md`) is DbExplorer's SOURCE REPOSITORY, read
/// via GitLab MCP tools — not a running instance's host:port, which is not recorded anywhere
/// in this workspace. The one thing that IS confirmed, because it comes from that cache's own
/// "Load-bearing invariants" table rather than a guess, is the degradation contract below:
/// <c>/api/diagnostics/*</c> needs the Profiler policy, and an Reader-scoped token 403s on it.
/// Every field name on <see cref="DbDiagnosticRecord"/> and the DTOs below is a best-effort
/// placeholder with no cached or probed shape behind it at all — confirm them against a real
/// response before trusting this source's SUCCESS path in an incident; the Skip path is solid.
/// </summary>
public sealed class DbDiagnosticsSource : IEvidenceSource
{
    public const string SourceName = "dbexplorer";

    private readonly IDbExplorerClient? client;

    /// <summary>Null when DbExplorer was not opted into for this run.</summary>
    public DbDiagnosticsSource(IDbExplorerClient? client) => this.client = client;

    public string Name => SourceName;

    public bool IsOffline => false;

    public async Task<SourceResult> CollectAsync(IncidentWindow window, IReadOnlyList<ServiceManifest> services, CancellationToken ct)
    {
        if (this.client is null)
        {
            return SourceResult.Skipped(
                SourceName,
                "DbExplorer is opt-in and not configured for this run — pass --online with "
                + "INCIDENTTIMELINE_DBEXPLORER_URL and INCIDENTTIMELINE_DBEXPLORER_TOKEN to query it.");
        }

        (IReadOnlyList<DbDiagnosticRecord>? records, string? error, bool forbidden) =
            await this.client.GetDiagnosticsAsync(window.FromUtc, window.ToUtc, ct).ConfigureAwait(false);

        if (forbidden)
        {
            return SourceResult.Skipped(SourceName, "token lacks the Profiler scope; database diagnostics not collected");
        }

        if (records is null)
        {
            return SourceResult.Skipped(SourceName, $"Could not reach DbExplorer: {error}");
        }

        TimelineEvent[] events =
        [
            .. records
                .Where(r => window.Contains(r.AtUtc))
                .Select(BuildEvent)
                .OrderBy(e => e.At),
        ];

        return SourceResult.Ok(SourceName, events, $"{events.Length} diagnostic record(s) in window.");
    }

    private static TimelineEvent BuildEvent(DbDiagnosticRecord r) => new()
    {
        At = r.AtUtc,
        Kind = string.Equals(r.Kind, "Deadlock", StringComparison.OrdinalIgnoreCase) ? EventKind.DbDeadlock : EventKind.DbBlocking,
        Confidence = Confidence.Reported,
        Service = "*",
        Summary = r.Detail,
        Provenance = $"dbexplorer:diagnostics@{r.AtUtc:O}",
        Caveat = "Reported by DbExplorer — a remote system this tool cannot independently re-check.",
    };
}

/// <summary>
/// Calls DbExplorer's real endpoints directly. See the class-level GOTCHA on
/// <see cref="DbDiagnosticsSource"/>: the DTO shape below is unverified against any real
/// response — only the 403/Profiler-scope degradation contract is confirmed.
/// </summary>
public sealed class HttpDbExplorerClient : IDbExplorerClient
{
    private readonly HttpClient http;

    /// <summary><paramref name="http"/> must already carry BaseAddress and, if configured, an
    /// <c>Authorization: Bearer dbx_…</c> header (the exemplar cache's own confirmed invariant
    /// for this API's auth scheme) — set up once by the caller, not by this class.</summary>
    public HttpDbExplorerClient(HttpClient http) => this.http = http;

    public async Task<(IReadOnlyList<DbDiagnosticRecord>? Records, string? Error, bool Forbidden)> GetDiagnosticsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        (IReadOnlyList<DbDiagnosticRecord>? blocking, string? blockingError, bool blockingForbidden) =
            await this.GetOneAsync("blocking", "Blocking", fromUtc, toUtc, ct).ConfigureAwait(false);
        if (blockingForbidden || blocking is null)
        {
            return (null, blockingError, blockingForbidden);
        }

        (IReadOnlyList<DbDiagnosticRecord>? deadlocks, string? deadlockError, bool deadlockForbidden) =
            await this.GetOneAsync("deadlocks", "Deadlock", fromUtc, toUtc, ct).ConfigureAwait(false);
        if (deadlockForbidden || deadlocks is null)
        {
            return (null, deadlockError, deadlockForbidden);
        }

        return ([.. blocking, .. deadlocks], null, false);
    }

    private async Task<(IReadOnlyList<DbDiagnosticRecord>?, string?, bool Forbidden)> GetOneAsync(
        string endpoint, string kindLabel, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        try
        {
            string path = $"/api/diagnostics/{endpoint}?from={Uri.EscapeDataString(fromUtc.ToString("O"))}"
                + $"&to={Uri.EscapeDataString(toUtc.ToString("O"))}";
            using HttpResponseMessage response = await this.http.GetAsync(path, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                return (null, "token lacks the Profiler scope; database diagnostics not collected", true);
            }

            if (!response.IsSuccessStatusCode)
            {
                return (null, $"HTTP {(int)response.StatusCode}", false);
            }

            DbExplorerItemsDto? dto = await response.Content.ReadFromJsonAsync<DbExplorerItemsDto>(cancellationToken: ct).ConfigureAwait(false);
            IReadOnlyList<DbDiagnosticRecord> records =
            [
                .. (dto?.Items ?? []).Select(i => new DbDiagnosticRecord { AtUtc = i.ObservedAtUtc, Kind = kindLabel, Detail = i.Description }),
            ];
            return (records, null, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return (null, ex.Message, false);
        }
    }
}

internal sealed record DbExplorerItemsDto(IReadOnlyList<DbExplorerRecordDto> Items);

internal sealed record DbExplorerRecordDto(DateTimeOffset ObservedAtUtc, string Description);
