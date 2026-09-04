namespace Aftermath.Tests;

using System.Net;
using Aftermath.Contracts;
using Aftermath.Sources;

public sealed class OctopusDeploySourceTests
{
    private static readonly IncidentWindow Window = new()
    {
        AtUtc = new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero),
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    private static ServiceManifest Manifest(string key, string? slug) => new() { Key = key, RepoPath = "x", OctopusProjectSlug = slug };

    [Fact]
    public async Task Skipped_when_not_configured_for_this_run()
    {
        var source = new OctopusDeploySource(client: null);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service", "ledger-core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("--online", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_with_no_octopus_slug_are_named_not_queried()
    {
        var client = new FakeOctopusClient(_ => ((IReadOnlyList<OctopusDeployment>?)[], null));
        var source = new OctopusDeploySource(client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("shared", null)], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Empty(client.Invocations);
        Assert.Contains("No Octopus project for: shared", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deployments_in_window_become_deploy_events_at_reported_confidence()
    {
        OctopusDeployment[] deployments =
        [
            new() { ReleaseVersion = "1.15.0", EnvironmentName = "Production", DeployedAtUtc = Window.AtUtc.AddMinutes(-40), State = "Success", DeploymentId = "Deployments-5501" },
        ];
        var client = new FakeOctopusClient(_ => (deployments, null));
        var source = new OctopusDeploySource(client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service", "ledger-core-service")], CancellationToken.None);

        TimelineEvent evt = Assert.Single(result.Events);
        Assert.Equal(EventKind.Deploy, evt.Kind);
        Assert.Equal(Confidence.Reported, evt.Confidence);
        Assert.Equal("Deployed 1.15.0 to Production (Success)", evt.Summary);
        Assert.Equal("octopus:ledger-core-service@Deployments-5501", evt.Provenance);
    }

    [Fact]
    public async Task Deployments_outside_the_window_are_excluded()
    {
        OctopusDeployment[] deployments =
        [
            new() { ReleaseVersion = "1.0.0", EnvironmentName = "Production", DeployedAtUtc = Window.AtUtc.AddDays(-30), State = "Success", DeploymentId = "Deployments-1" },
        ];
        var client = new FakeOctopusClient(_ => (deployments, null));
        var source = new OctopusDeploySource(client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service", "ledger-core-service")], CancellationToken.None);

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task A_failed_project_query_is_named_but_does_not_fail_the_whole_source()
    {
        var client = new FakeOctopusClient(_ => ((IReadOnlyList<OctopusDeployment>?)null, "401 Unauthorized"));
        var source = new OctopusDeploySource(client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service", "ledger-core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Contains("core-service: 401 Unauthorized", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_is_online_not_offline()
    {
        Assert.False(new OctopusDeploySource(null).IsOffline);
        Assert.Equal("octopus", new OctopusDeploySource(null).Name);
    }
}

/// <summary>Proves HttpOctopusClient's project -> deployments -> release -> environment join
/// against the recorded fixture shape, with the network unplugged.</summary>
public sealed class HttpOctopusClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "http", name));

    [Fact]
    public async Task Joins_project_deployments_releases_and_environments_into_deployments()
    {
        var handler = new StubHttpMessageHandler(path => path switch
        {
            var p when p.Contains("/projects/ledger-core-service", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, Fixture("octopus-project.json")),
            var p when p.Contains("/deployments?", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, Fixture("octopus-deployments.json")),
            var p when p.Contains("/environments", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, Fixture("octopus-environments.json")),
            var p when p.Contains("Releases-8842", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, Fixture("octopus-release-8842.json")),
            var p when p.Contains("Releases-8801", StringComparison.Ordinal) =>
                (HttpStatusCode.OK, Fixture("octopus-release-8801.json")),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://octopus.example.invalid") };
        var client = new HttpOctopusClient(http);

        (IReadOnlyList<OctopusDeployment>? deployments, string? error) = await client.GetDeploymentsAsync("ledger-core-service", CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(deployments);
        Assert.Equal(2, deployments!.Count);
        Assert.Contains(deployments, d => d.ReleaseVersion == "1.15.0" && d.EnvironmentName == "Production");
        Assert.Contains(deployments, d => d.ReleaseVersion == "1.14.0" && d.EnvironmentName == "Production");
    }

    [Fact]
    public async Task Unauthorised_project_lookup_returns_an_error_not_an_exception()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.Unauthorized, "{\"ErrorMessage\":\"You must be logged in\"}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://octopus.example.invalid") };
        var client = new HttpOctopusClient(http);

        (IReadOnlyList<OctopusDeployment>? deployments, string? error) = await client.GetDeploymentsAsync("ledger-core-service", CancellationToken.None);

        Assert.Null(deployments);
        Assert.NotNull(error);
    }
}
