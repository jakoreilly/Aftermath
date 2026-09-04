namespace Aftermath.Logs;

using Aftermath.Contracts;

/// <summary>One WARN/ERROR/FATAL line, parsed but not yet an event — the input to
/// <see cref="LogClusterer"/>. Raw lines are noise (§3.7); this is the shape clustering groups
/// before anything is added to the timeline.</summary>
public sealed record RawLogLine
{
    public required DateTimeOffset At { get; init; }

    public required EventKind Kind { get; init; }

    public required Confidence Confidence { get; init; }

    public string? Caveat { get; init; }

    public required string Message { get; init; }

    public required string Provenance { get; init; }

    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? CorrelationPrefix { get; init; }

    public string? Logger { get; init; }
}
