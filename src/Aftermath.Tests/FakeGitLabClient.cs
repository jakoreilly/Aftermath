namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>Canned <see cref="IGitLabClient"/> for offline tests.</summary>
public sealed class FakeGitLabClient(Func<string, (IReadOnlyList<GitLabPipeline>? Pipelines, string? Error)> respond) : IGitLabClient
{
    public List<string> Invocations { get; } = [];

    public Task<(IReadOnlyList<GitLabPipeline>? Pipelines, string? Error)> GetPipelinesAsync(
        string projectPath, DateTimeOffset updatedAfterUtc, DateTimeOffset updatedBeforeUtc, CancellationToken ct)
    {
        this.Invocations.Add(projectPath);
        return Task.FromResult(respond(projectPath));
    }
}
