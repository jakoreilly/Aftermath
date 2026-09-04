namespace Aftermath.Contracts;

/// <summary>How much a single event can be trusted. Set by the source that produced it,
/// never upgraded downstream.</summary>
public enum Confidence
{
    /// <summary>Read directly from a durable artefact: a git object, a log line on disk.</summary>
    Observed,

    /// <summary>Derived by this tool from something observed — e.g. a UTC instant computed
    /// from a local time plus a filename date. Correct only if the derivation held.</summary>
    Inferred,

    /// <summary>Asserted by a remote system we cannot re-check — an Octopus deployment
    /// record, an operator's note.</summary>
    Reported,
}

public enum EventKind
{
    Release,
    Commit,
    Deploy,
    LogError,
    LogWarning,
    LogFatal,
    ServiceStart,
    ServiceStop,
    DbBlocking,
    DbDeadlock,
    Operator,

    /// <summary>One per-service, per-minute (or per-service "unavailable") bucket of request
    /// count / 4xx / 5xx / p50 / p95, recovered from `HTTP Response:` log lines with no
    /// metrics store (plan.md §3.6a).</summary>
    HttpMetrics,

    /// <summary>A GitLab CI pipeline result (Phase 7) — GitLab holds no deployment record for
    /// this estate (zero `environment:` keys in any `.gitlab-ci.yml`), so this is the pass/fail
    /// signal, not a deploy. Only failures are surfaced; a stream of "pipeline succeeded" for
    /// every commit would be noise with no more signal than the commit itself already carries.</summary>
    CiPipeline,
}

/// <summary>
/// One thing that happened. <see cref="At"/> is ALWAYS UTC (constraint 5) and
/// <see cref="Provenance"/> is ALWAYS resolvable by a human — a file:line, a git ref, or a
/// URL. There is no way to construct one of these without both.
/// </summary>
public sealed record TimelineEvent
{
    public required DateTimeOffset At { get; init; }

    public required EventKind Kind { get; init; }

    public required Confidence Confidence { get; init; }

    /// <summary>Repo/service key from <see cref="ServiceManifest.Key"/>; "*" for estate-wide.</summary>
    public required string Service { get; init; }

    /// <summary>One line, already redacted-safe in wording (no secrets interpolated).</summary>
    public required string Summary { get; init; }

    /// <summary>Where a human goes to check this. "core-service/CHANGELOG.md:1",
    /// "billing-service@v2.10.0", "logs/…_2026-09-03.log:4412".</summary>
    public required string Provenance { get; init; }

    /// <summary>Set when the source recorded a caveat that changes how the event reads —
    /// e.g. an ambiguous DST local time, or a truncated log file.</summary>
    public string? Caveat { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    /// <summary>The log4net AcmeLogPrefix field. On web services this is the raw
    /// Pz-Authorisation session token (LedgerLogPrefixMiddleware.cs:35-36) — it MUST be
    /// pseudonymised before rendering. See hard constraints 3 and 4.</summary>
    public string? CorrelationPrefix { get; init; }

    public IReadOnlyList<string> Tickets { get; init; } = [];

    public string? Detail { get; init; }
}
