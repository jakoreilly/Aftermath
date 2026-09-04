namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>Canned <see cref="IGitHubClient"/> for offline tests.</summary>
public sealed class FakeGitHubClient(Func<string, (IReadOnlyList<GitHubRun>? Runs, string? Error)> respond) : IGitHubClient
{
    public List<string> Invocations { get; } = [];

    public Task<(IReadOnlyList<GitHubRun>? Runs, string? Error)> GetWorkflowRunsAsync(
        string repoSlug, DateTimeOffset createdAfterUtc, DateTimeOffset createdBeforeUtc, CancellationToken ct)
    {
        this.Invocations.Add(repoSlug);
        return Task.FromResult(respond(repoSlug));
    }
}
