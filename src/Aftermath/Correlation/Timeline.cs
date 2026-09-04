namespace Aftermath.Correlation;

using Aftermath.Contracts;
using Aftermath.Sources;

/// <summary>Every event a `collect` run's sources produced, merged into one de-duplicated,
/// totally-ordered view — the input to <see cref="SuspectRanker"/> and, later, Phase 5's
/// document. <see cref="Sources"/> is carried through so the document can also say what was
/// NOT looked at (a Skipped/Failed source), not only what was found.</summary>
public sealed record Timeline
{
    public required IReadOnlyList<TimelineEvent> Events { get; init; }

    public required IReadOnlyList<SourceResult> Sources { get; init; }

    public required IncidentWindow Window { get; init; }

    /// <summary>Events sharing a `TraceId`, grouped for cross-service correlation. Only 15 of
    /// the estate's 22 log patterns carry a trace_id at all (Phase 3), so this is an
    /// enrichment layered on top of time proximity, never the backbone — an event with no
    /// group is not evidence it is unrelated to anything.</summary>
    public required IReadOnlyList<TraceGroup> TraceGroups { get; init; }
}

/// <summary>One `TraceId` and every event carrying it, ordered the same way the parent
/// timeline is.</summary>
public sealed record TraceGroup
{
    public required string TraceId { get; init; }

    public required IReadOnlyList<TimelineEvent> Events { get; init; }
}
