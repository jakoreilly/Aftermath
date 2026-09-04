namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>Canned <see cref="IDbExplorerClient"/> for offline tests.</summary>
public sealed class FakeDbExplorerClient(
    Func<DateTimeOffset, DateTimeOffset, (IReadOnlyList<DbDiagnosticRecord>? Records, string? Error, bool Forbidden)> respond)
    : IDbExplorerClient
{
    public Task<(IReadOnlyList<DbDiagnosticRecord>? Records, string? Error, bool Forbidden)> GetDiagnosticsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        Task.FromResult(respond(fromUtc, toUtc));
}
