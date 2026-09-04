namespace Aftermath.Sources;

using Aftermath.Contracts;

public enum SourceStatus { Ok, Skipped, Failed }

/// <summary>Result of one source's collection. A source NEVER throws to the caller: a
/// partial timeline with a named gap is useful, an exception is not. Phase 5 renders
/// every non-Ok status into the document so the reader knows what was NOT looked at.</summary>
public sealed record SourceResult
{
    public required string SourceName { get; init; }
    public required SourceStatus Status { get; init; }

    /// <summary>On Skipped/Failed: what was not read and what the operator can do about it.</summary>
    public required string Message { get; init; }

    public IReadOnlyList<TimelineEvent> Events { get; init; } = [];

    public static SourceResult Ok(string name, IReadOnlyList<TimelineEvent> events, string message) =>
        new() { SourceName = name, Status = SourceStatus.Ok, Events = events, Message = message };

    public static SourceResult Skipped(string name, string message) =>
        new() { SourceName = name, Status = SourceStatus.Skipped, Message = message };

    public static SourceResult Failed(string name, string message) =>
        new() { SourceName = name, Status = SourceStatus.Failed, Message = message };
}

public interface IEvidenceSource
{
    string Name { get; }

    /// <summary>False for anything that opens a socket. The default CLI run includes only
    /// offline sources; --online opts the rest in.</summary>
    bool IsOffline { get; }

    Task<SourceResult> CollectAsync(
        IncidentWindow window,
        IReadOnlyList<ServiceManifest> services,
        CancellationToken ct);
}
