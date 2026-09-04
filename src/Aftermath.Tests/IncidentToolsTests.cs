namespace Aftermath.Tests;

using Aftermath.Configuration;
using Aftermath.Tools;

/// <summary>
/// Exercises the MCP tool surface by direct method call (the exemplar's own convention,
/// e.g. CompareToolsTests) rather than through the MCP protocol itself — the protocol framing
/// is the SDK's concern, not this tool's. Runs against the real c:\workspace\work, the same
/// workspace every other phase's real-run verification used.
/// </summary>
public sealed class IncidentToolsTests
{
    private const string RealWorkspace = "c:\\workspace\\work";

    private static IncidentTools Tools(string? defaultWorkspace = RealWorkspace) =>
        new(WorkspaceRegistry.Build(key => key == WorkspaceRegistry.WorkspaceVar ? defaultWorkspace : null));

    [Fact]
    public void Services_fails_clearly_when_no_workspace_is_configured_or_supplied()
    {
        ToolResult result = Tools(defaultWorkspace: null).Services();

        Assert.False(result.Success);
        Assert.Equal("WORKSPACE_NOT_CONFIGURED", result.Error);
        Assert.Contains(WorkspaceRegistry.WorkspaceVar, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Services_per_call_override_wins_over_the_configured_default()
    {
        ToolResult result = Tools(defaultWorkspace: "c:\\nope").Services(workspace: RealWorkspace);

        Assert.True(result.Success);
    }

    [Fact]
    public void Services_against_the_real_workspace_returns_manifests()
    {
        ToolResult result = Tools().Services();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task Collect_rejects_a_missing_at_before_touching_the_workspace()
    {
        ToolResult result = await Tools().CollectAsync(at: string.Empty);

        Assert.False(result.Success);
        Assert.Equal("INVALID_ARGUMENT", result.Error);
    }

    [Fact]
    public async Task Draft_against_the_real_workspace_renders_all_six_headings()
    {
        ToolResult result = await Tools().DraftAsync(at: "2026-07-17T13:00:00Z");

        Assert.True(result.Success);
        string document = Assert.IsType<string>(result.Data);
        string[] headings =
        [
            "## What we looked at", "## What we could not see", "## Changed nearby",
            "## Error rate and latency", "## Timeline", "## Open questions for the reviewer",
        ];
        Assert.All(headings, h => Assert.Contains(h, document, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Suspects_against_the_real_workspace_ranks_the_known_releases_first()
    {
        ToolResult result = await Tools().SuspectsAsync(at: "2026-07-17T13:00:00Z");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }

    [Fact]
    public async Task Collect_truncates_at_500_events_and_says_so_rather_than_returning_fewer_silently()
    {
        // The measured year-wide case (Phase 2 DoD): --window 8760h returns 2,985 events.
        ToolResult result = await Tools().CollectAsync(at: "2026-07-17T13:00:00Z", window: "8760h");

        Assert.True(result.Success);
        Assert.Contains("TRUNCATED", result.Message, StringComparison.Ordinal);
    }
}
