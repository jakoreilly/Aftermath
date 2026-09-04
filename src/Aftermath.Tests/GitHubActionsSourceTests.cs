namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Sources;

public sealed class GitHubActionsSourceTests
{
    private static readonly IncidentWindow Window = new()
    {
        AtUtc = new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero),
        LookBack = TimeSpan.FromHours(24),
        LookForward = TimeSpan.FromHours(2),
    };

    private static ServiceManifest Manifest(string key) => new() { Key = key, RepoPath = key };

    private static FakeGitRunner RemoteRunner(string remoteUrl) =>
        new(args => args.Contains("remote") ? new GitResult(0, remoteUrl + "\n", string.Empty) : new GitResult(1, string.Empty, string.Empty));

    private static GitHubRun Run(string? conclusion, DateTimeOffset updatedAt, long id = 30433642, string sha = "acb5820ced9479c074f688cc328bf03f341a511d") => new()
    {
        Id = id,
        WorkflowName = "CI",
        HeadSha = sha,
        HeadBranch = "main",
        Status = "completed",
        Conclusion = conclusion,
        UpdatedAtUtc = updatedAt,
        HtmlUrl = $"https://github.com/acme-org/core-service/actions/runs/{id}",
    };

    [Fact]
    public async Task Skipped_when_not_configured_for_this_run()
    {
        var source = new GitHubActionsSource(RemoteRunner("git@github.com:acme-org/core-service.git"), client: null);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("--online", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_with_no_resolvable_remote_are_named_not_queried()
    {
        var git = new FakeGitRunner(_ => new GitResult(128, string.Empty, "fatal: not a git repository"));
        var client = new FakeGitHubClient(_ => ((IReadOnlyList<GitHubRun>?)[], null));
        var source = new GitHubActionsSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("shared-master")], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Empty(client.Invocations);
        Assert.Contains("No resolvable GitHub remote for: shared-master", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_github_remote_is_treated_as_no_resolvable_remote()
    {
        var git = RemoteRunner("git@bull.acme.example:acme-group/platform/services/core-service.git");
        var client = new FakeGitHubClient(_ => ((IReadOnlyList<GitHubRun>?)[], null));
        var source = new GitHubActionsSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Empty(client.Invocations);
        Assert.Contains("No resolvable GitHub remote for: core-service", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_failed_runs_in_window_become_events()
    {
        GitHubRun[] runs =
        [
            Run("success", Window.AtUtc.AddMinutes(-30), id: 111),
            Run("failure", Window.AtUtc.AddMinutes(-10), id: 222, sha: "35a1e3548ec92fe7eba8abccfe1963756ee2d60c"),
            Run("failure", Window.AtUtc.AddDays(-5), id: 333),
        ];
        var git = RemoteRunner("git@github.com:acme-org/core-service.git");
        var client = new FakeGitHubClient(_ => (runs, null));
        var source = new GitHubActionsSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        TimelineEvent evt = Assert.Single(result.Events);
        Assert.Equal(EventKind.CiPipeline, evt.Kind);
        Assert.Equal(Confidence.Reported, evt.Confidence);
        Assert.Contains("35a1e35", evt.Summary, StringComparison.Ordinal);
        Assert.Equal("https://github.com/acme-org/core-service/actions/runs/222", evt.Provenance);
        Assert.Equal("acme-org/core-service#222", evt.Detail);
    }

    [Fact]
    public async Task In_progress_runs_with_a_null_conclusion_are_ignored()
    {
        GitHubRun[] runs = [Run(conclusion: null, Window.AtUtc.AddMinutes(-5))];
        var git = RemoteRunner("git@github.com:acme-org/core-service.git");
        var client = new FakeGitHubClient(_ => (runs, null));
        var source = new GitHubActionsSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Client_error_is_reported_per_service_not_thrown()
    {
        var git = RemoteRunner("git@github.com:acme-org/core-service.git");
        var client = new FakeGitHubClient(_ => (null, "403 rate limit exceeded"));
        var source = new GitHubActionsSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Empty(result.Events);
        Assert.Contains("core-service: 403 rate limit exceeded", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolved_repo_slug_is_passed_to_the_client()
    {
        var git = RemoteRunner("https://github.com/acme-org/core-service.git");
        var client = new FakeGitHubClient(_ => ((IReadOnlyList<GitHubRun>?)[], null));
        var source = new GitHubActionsSource(git, client);

        await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Contains("acme-org/core-service", client.Invocations);
    }

    [Fact]
    public void Source_is_online_not_offline()
    {
        var git = RemoteRunner("git@github.com:x/y.git");
        Assert.False(new GitHubActionsSource(git, null).IsOffline);
        Assert.Equal("github", new GitHubActionsSource(git, null).Name);
    }
}
