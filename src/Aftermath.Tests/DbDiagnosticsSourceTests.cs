namespace Aftermath.Tests;

using System.Net;
using Aftermath.Contracts;
using Aftermath.Sources;

public sealed class DbDiagnosticsSourceTests
{
    private static readonly IncidentWindow Window = new()
    {
        AtUtc = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero),
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    [Fact]
    public async Task Skipped_when_not_configured_for_this_run()
    {
        var source = new DbDiagnosticsSource(client: null);

        SourceResult result = await source.CollectAsync(Window, [], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("--online", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_scoped_token_skips_with_the_documented_degradation_message_not_a_failure()
    {
        var client = new FakeDbExplorerClient((_, _) => (null, "forbidden", true));
        var source = new DbDiagnosticsSource(client);

        SourceResult result = await source.CollectAsync(Window, [], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Equal("token lacks the Profiler scope; database diagnostics not collected", result.Message);
    }

    [Fact]
    public async Task Blocking_and_deadlock_records_become_estate_wide_reported_events()
    {
        DbDiagnosticRecord[] records =
        [
            new() { AtUtc = Window.AtUtc.AddMinutes(-30), Kind = "Blocking", Detail = "SPID 84 blocked SPID 91" },
            new() { AtUtc = Window.AtUtc.AddMinutes(-20), Kind = "Deadlock", Detail = "Deadlock victim SPID 91" },
        ];
        var client = new FakeDbExplorerClient((_, _) => (records, null, false));
        var source = new DbDiagnosticsSource(client);

        SourceResult result = await source.CollectAsync(Window, [], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Equal(2, result.Events.Count);
        Assert.Contains(result.Events, e => e.Kind == EventKind.DbBlocking && e.Service == "*");
        Assert.Contains(result.Events, e => e.Kind == EventKind.DbDeadlock && e.Service == "*");
        Assert.All(result.Events, e => Assert.Equal(Confidence.Reported, e.Confidence));
    }

    [Fact]
    public async Task A_non_forbidden_failure_skips_rather_than_fails()
    {
        var client = new FakeDbExplorerClient((_, _) => (null, "connection refused", false));
        var source = new DbDiagnosticsSource(client);

        SourceResult result = await source.CollectAsync(Window, [], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("connection refused", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_is_online_not_offline()
    {
        Assert.False(new DbDiagnosticsSource(null).IsOffline);
        Assert.Equal("dbexplorer", new DbDiagnosticsSource(null).Name);
    }
}

/// <summary>Proves HttpDbExplorerClient's blocking+deadlock join and the 403 degradation path,
/// with the network unplugged.</summary>
public sealed class HttpDbExplorerClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "http", name));

    [Fact]
    public async Task Combines_blocking_and_deadlock_records()
    {
        var handler = new StubHttpMessageHandler(path => path.Contains("/blocking", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Fixture("dbexplorer-blocking.json"))
            : (HttpStatusCode.OK, Fixture("dbexplorer-deadlocks.json")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://dbexplorer.example.invalid") };
        var client = new HttpDbExplorerClient(http);

        (IReadOnlyList<DbDiagnosticRecord>? records, string? error, bool forbidden) =
            await client.GetDiagnosticsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.False(forbidden);
        Assert.Null(error);
        DbDiagnosticRecord record = Assert.Single(records!);
        Assert.Equal("Blocking", record.Kind);
        Assert.Contains("SPID 84 blocked SPID 91", record.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_403_on_blocking_is_reported_as_forbidden_not_a_generic_error()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.Forbidden, string.Empty));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://dbexplorer.example.invalid") };
        var client = new HttpDbExplorerClient(http);

        (IReadOnlyList<DbDiagnosticRecord>? records, string? error, bool forbidden) =
            await client.GetDiagnosticsAsync(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.True(forbidden);
        Assert.Null(records);
        Assert.Contains("Profiler", error, StringComparison.Ordinal);
    }
}
