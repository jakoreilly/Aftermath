namespace Aftermath.Contracts;

/// <summary>The incident under investigation. Bounds are explicit rather than implied so
/// every source windows identically and the report can state what it looked at.</summary>
public sealed record IncidentWindow
{
    public required DateTimeOffset AtUtc { get; init; }

    public required TimeSpan LookBack { get; init; }

    public required TimeSpan LookForward { get; init; }

    public DateTimeOffset FromUtc => AtUtc - LookBack;

    public DateTimeOffset ToUtc => AtUtc + LookForward;

    public bool Contains(DateTimeOffset t) => t >= FromUtc && t <= ToUtc;

    /// <summary>Defaults derived from measured release cadence — see the plan's Phase 4
    /// design note. Median inter-release gap across seven repos is 363-988h, so a 24h
    /// look-back contains a release only 3-7% of the time and a release inside the window
    /// is a rare, high-signal coincidence. Do not change either value without redoing that
    /// measurement.</summary>
    public static IncidentWindow Default(DateTimeOffset atUtc) => new()
    {
        AtUtc = atUtc,
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };
}
