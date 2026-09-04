namespace Aftermath.Configuration;

using Aftermath.Cli;
using Aftermath.Tools;

/// <summary>
/// Resolves the workspace/log-root/timezone defaults an MCP host configures once, via its own
/// env block — a stdio MCP session receives no command-line arguments to carry --workspace on
/// (<c>Program.cs</c> branches to the one-shot CLI whenever args are present, exactly as the
/// exemplar does). Modelled on <c>Acme.ClaudeDb.Configuration.EnvironmentRegistry</c>: a
/// pure factory over a variable accessor, because
/// <see cref="Environment.GetEnvironmentVariable(string)"/> itself is static and unmockable —
/// the one real call site lives in <c>Program.cs</c> and is not unit tested.
///
/// Each tool call may still override any of these per-call; the registry only supplies the
/// default when a call omits them.
/// </summary>
public sealed class WorkspaceRegistry
{
    public const string WorkspaceVar = "INCIDENTTIMELINE_WORKSPACE";
    public const string LogRootVar = "INCIDENTTIMELINE_LOG_ROOT";
    public const string TimezoneVar = "INCIDENTTIMELINE_TIMEZONE";

    private WorkspaceRegistry(string? workspace, string? logRoot, string? timezone, OnlineSourceOptions onlineDefaults)
    {
        this.Workspace = workspace;
        this.LogRoot = logRoot;
        this.Timezone = timezone;
        this.OnlineDefaults = onlineDefaults;
    }

    public string? Workspace { get; }

    public string? LogRoot { get; }

    public string? Timezone { get; }

    /// <summary>Credentials for the three opt-in network sources (Phase 7), with
    /// <see cref="OnlineSourceOptions.Online"/> left false — a tool call sets that explicitly
    /// per call; the registry only ever supplies the credentials underneath it.</summary>
    public OnlineSourceOptions OnlineDefaults { get; }

    /// <summary>Pure — no process state is read here.</summary>
    public static WorkspaceRegistry Build(Func<string, string?> getVar)
    {
        ArgumentNullException.ThrowIfNull(getVar);
        var onlineDefaults = new OnlineSourceOptions
        {
            OctopusUrl = getVar(OnlineSourceOptions.OctopusUrlVar),
            OctopusToken = getVar(OnlineSourceOptions.OctopusTokenVar),
            DbExplorerUrl = getVar(OnlineSourceOptions.DbExplorerUrlVar),
            DbExplorerToken = getVar(OnlineSourceOptions.DbExplorerTokenVar),
            GitLabUrl = getVar(OnlineSourceOptions.GitLabUrlVar),
            GitLabToken = getVar(OnlineSourceOptions.GitLabTokenVar),
        };
        return new WorkspaceRegistry(getVar(WorkspaceVar), getVar(LogRootVar), getVar(TimezoneVar), onlineDefaults);
    }

    /// <summary>Resolves a per-call workspace override against the configured default, in a
    /// ready-to-return failure shape rather than throwing — a Skipped source is useful during
    /// an incident, an unhandled exception in a tool call is not.</summary>
    public (string? Workspace, ToolResult? Failure) ResolveWorkspace(string? requested)
    {
        string? workspace = string.IsNullOrWhiteSpace(requested) ? this.Workspace : requested.Trim();
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            return (workspace, null);
        }

        return (null, ToolResult.Fail(
            "WORKSPACE_NOT_CONFIGURED",
            $"No workspace configured. Set {WorkspaceVar} in the MCP host's env block, or pass "
            + "workspace explicitly on this call."));
    }
}
