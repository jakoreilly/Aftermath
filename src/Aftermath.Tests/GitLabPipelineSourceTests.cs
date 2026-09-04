namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Sources;

public sealed class GitLabPipelineSourceTests
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

    [Fact]
    public async Task Skipped_when_not_configured_for_this_run()
    {
        var source = new GitLabPipelineSource(RemoteRunner("git@bull.acme.example:x/y.git"), client: null);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("--online", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Services_with_no_resolvable_remote_are_named_not_queried()
    {
        var git = new FakeGitRunner(_ => new GitResult(128, string.Empty, "fatal: not a git repository"));
        var client = new FakeGitLabClient(_ => ((IReadOnlyList<GitLabPipeline>?)[], null));
        var source = new GitLabPipelineSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("shared-master")], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Empty(client.Invocations);
        Assert.Contains("No resolvable GitLab remote for: shared-master", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_failed_pipelines_in_window_become_events()
    {
        GitLabPipeline[] pipelines =
        [
            new() { Id = 214254, Sha = "80f1ce438a7e35996e38359cc735a445d8eb85cb", Ref = "master", Status = "success", UpdatedAtUtc = Window.AtUtc.AddMinutes(-30), WebUrl = "https://bull.acme.example/x/-/pipelines/214254" },
            new() { Id = 211953, Sha = "35a1e3548ec92fe7eba8abccfe1963756ee2d60c", Ref = "master", Status = "failed", UpdatedAtUtc = Window.AtUtc.AddMinutes(-10), WebUrl = "https://bull.acme.example/x/-/pipelines/211953" },
        ];
        var git = RemoteRunner("git@bull.acme.example:acme-group/platform/services/core-service.git");
        var client = new FakeGitLabClient(_ => (pipelines, null));
        var source = new GitLabPipelineSource(git, client);

        SourceResult result = await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        TimelineEvent evt = Assert.Single(result.Events);
        Assert.Equal(EventKind.CiPipeline, evt.Kind);
        Assert.Equal(Confidence.Reported, evt.Confidence);
        Assert.Contains("35a1e35", evt.Summary, StringComparison.Ordinal);
        Assert.Equal("https://bull.acme.example/x/-/pipelines/211953", evt.Provenance);
    }

    [Fact]
    public async Task Resolved_project_path_is_passed_to_the_client()
    {
        var git = RemoteRunner("git@bull.acme.example:acme-group/platform/services/core-service.git");
        var client = new FakeGitLabClient(_ => ((IReadOnlyList<GitLabPipeline>?)[], null));
        var source = new GitLabPipelineSource(git, client);

        await source.CollectAsync(Window, [Manifest("core-service")], CancellationToken.None);

        Assert.Contains("acme-group/platform/services/core-service", client.Invocations);
    }

    [Fact]
    public void Source_is_online_not_offline()
    {
        var git = RemoteRunner("git@bull.acme.example:x/y.git");
        Assert.False(new GitLabPipelineSource(git, null).IsOffline);
        Assert.Equal("gitlab", new GitLabPipelineSource(git, null).Name);
    }
}
