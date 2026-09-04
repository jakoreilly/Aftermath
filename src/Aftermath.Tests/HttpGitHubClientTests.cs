namespace Aftermath.Tests;

using System.Net;
using Aftermath.Sources;

/// <summary>Proves HttpGitHubClient's list-workflow-runs flow against a hand-written copy of
/// GitHub's documented REST v3 response for GET /repos/{owner}/{repo}/actions/runs (see the
/// NOTE on GitHubActionsSource — this shape is documented, not probed), network unplugged.</summary>
public sealed class HttpGitHubClientTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "http", name));

    [Fact]
    public async Task Lists_runs_and_normalises_timestamps_to_utc()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, Fixture("github-runs.json")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.invalid") };
        var client = new HttpGitHubClient(http);

        (IReadOnlyList<GitHubRun>? runs, string? error) = await client.GetWorkflowRunsAsync(
            "acme-org/core-service", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.Null(error);
        Assert.NotNull(runs);
        Assert.Equal(2, runs!.Count);
        Assert.Contains(runs, r => r.Conclusion == "failure" && r.HeadSha == "35a1e3548ec92fe7eba8abccfe1963756ee2d60c");
        Assert.Contains(runs, r => r.Conclusion == "success");
        Assert.All(runs, r => Assert.Equal(TimeSpan.Zero, r.UpdatedAtUtc.Offset));
    }

    [Fact]
    public async Task Requests_the_repo_scoped_runs_path_with_a_created_range()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.OK, Fixture("github-runs.json")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.invalid") };
        var client = new HttpGitHubClient(http);

        await client.GetWorkflowRunsAsync(
            "acme-org/core-service",
            new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        string path = Assert.Single(handler.RequestedPaths);
        Assert.StartsWith("/repos/acme-org/core-service/actions/runs?created=", path, StringComparison.Ordinal);
        Assert.Contains("2026-08-18", path, StringComparison.Ordinal);
        Assert.Contains("..", Uri.UnescapeDataString(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unresolvable_repo_returns_an_error_not_an_exception()
    {
        var handler = new StubHttpMessageHandler(_ => (HttpStatusCode.NotFound, "{}"));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.invalid") };
        var client = new HttpGitHubClient(http);

        (IReadOnlyList<GitHubRun>? runs, string? error) = await client.GetWorkflowRunsAsync(
            "no/such-repo", DateTimeOffset.MinValue, DateTimeOffset.MaxValue, CancellationToken.None);

        Assert.Null(runs);
        Assert.NotNull(error);
    }
}
