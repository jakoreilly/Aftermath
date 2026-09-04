namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>Canned <see cref="IOctopusClient"/> for offline tests — mirrors
/// <see cref="FakeGitRunner"/>'s shape for the same reason.</summary>
public sealed class FakeOctopusClient : IOctopusClient
{
    private readonly Func<string, (IReadOnlyList<OctopusDeployment>? Deployments, string? Error)> respond;

    public FakeOctopusClient(Func<string, (IReadOnlyList<OctopusDeployment>?, string?)> respond) => this.respond = respond;

    public List<string> Invocations { get; } = [];

    public Task<(IReadOnlyList<OctopusDeployment>? Deployments, string? Error)> GetDeploymentsAsync(string projectSlug, CancellationToken ct)
    {
        this.Invocations.Add(projectSlug);
        return Task.FromResult(this.respond(projectSlug));
    }
}
