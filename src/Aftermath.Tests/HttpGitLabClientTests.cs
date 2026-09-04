namespace Aftermath.Tests;

using System.Net;
using Aftermath.Sources;

/// <summary>Proves HttpGitLabClient's project-resolve-then-list-pipelines flow against a
/// recorded copy of GitLab's real, live response (captured 2026-09-04 — see the GOTCHA on
/// GitLabPipelineSource), with the network unplugged.</summary>
public sealed class HttpGitLabClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "http", name));

    [Fact]
    public async Task Resolves_project_then_lists_pipelines()
    {
        var handler = new StubHttpMessageHandler(path => path.Contains("/pipelines", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Fixture("gitlab-pipelines.json"))
            : (HttpStatusCode.OK, Fixture("gitlab-project.json")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.invalid") };
        var client = new HttpGitLabClient(http);

        (IReadOnlyList<GitLabPipeline>? pipelines, string? error) = await client.GetPipelinesAsync(
            "acme-group/platform/services/core-service", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(pipelines);
        Assert.Equal(2, pipelines!.Count);
        Assert.Contains(pipelines, p => p.Status == "failed" && p.Sha == "35a1e3548ec92fe7eba8abccfe1963756ee2d60c");
        Assert.Contains(pipelines, p => p.Status == "success");
        Assert.All(pipelines, p => Assert.True(p.UpdatedAtUtc.Offset == TimeSpan.Zero));
    }

    [Fact]
    public async Task Unresolvable_project_returns_an_error_not_an_exception()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.invalid") };
        var client = new HttpGitLabClient(http);

        (IReadOnlyList<GitLabPipeline>? pipelines, string? error) = await client.GetPipelinesAsync(
            "no/such/project", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.Null(pipelines);
        Assert.NotNull(error);
    }
}
