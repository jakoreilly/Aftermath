namespace Aftermath.Tests;

using Aftermath.Configuration;
using Aftermath.Tools;

public sealed class WorkspaceRegistryTests
{
    private static Func<string, string?> Vars(params (string Key, string Value)[] pairs) =>
        key => pairs.FirstOrDefault(p => p.Key == key).Value;

    [Fact]
    public void Build_reads_all_three_variables()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Build(Vars(
            (WorkspaceRegistry.WorkspaceVar, "c:\\workspace\\work"),
            (WorkspaceRegistry.LogRootVar, "c:\\incident\\logs"),
            (WorkspaceRegistry.TimezoneVar, "Europe/Dublin")));

        Assert.Equal("c:\\workspace\\work", registry.Workspace);
        Assert.Equal("c:\\incident\\logs", registry.LogRoot);
        Assert.Equal("Europe/Dublin", registry.Timezone);
    }

    [Fact]
    public void Build_leaves_unset_variables_null()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Build(_ => null);

        Assert.Null(registry.Workspace);
        Assert.Null(registry.LogRoot);
        Assert.Null(registry.Timezone);
    }

    [Fact]
    public void ResolveWorkspace_prefers_the_per_call_override()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Build(Vars((WorkspaceRegistry.WorkspaceVar, "c:\\default")));

        (string? workspace, ToolResult? failure) = registry.ResolveWorkspace("c:\\override");

        Assert.Equal("c:\\override", workspace);
        Assert.Null(failure);
    }

    [Fact]
    public void ResolveWorkspace_falls_back_to_the_configured_default()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Build(Vars((WorkspaceRegistry.WorkspaceVar, "c:\\default")));

        (string? workspace, ToolResult? failure) = registry.ResolveWorkspace(null);

        Assert.Equal("c:\\default", workspace);
        Assert.Null(failure);
    }

    [Fact]
    public void ResolveWorkspace_fails_clearly_when_nothing_is_configured()
    {
        WorkspaceRegistry registry = WorkspaceRegistry.Build(_ => null);

        (string? workspace, ToolResult? failure) = registry.ResolveWorkspace(null);

        Assert.Null(workspace);
        Assert.NotNull(failure);
        Assert.Equal("WORKSPACE_NOT_CONFIGURED", failure!.Error);
        Assert.Contains(WorkspaceRegistry.WorkspaceVar, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_rejects_a_null_accessor()
    {
        Assert.Throws<ArgumentNullException>(() => WorkspaceRegistry.Build(null!));
    }
}
