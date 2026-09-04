namespace Aftermath.Sources;

using System.Diagnostics;
using System.Text;

/// <summary>One completed git invocation. Non-zero exits are values, not exceptions —
/// "this directory is not a working copy" is evidence about the estate, and the source
/// turns it into a named gap rather than a stack trace (see <see cref="SourceResult"/>).</summary>
public sealed record GitResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>
/// The seam that keeps <see cref="GitReleaseSource"/> unit-testable. Tests supply verbatim
/// git output captured from the real estate; only <see cref="ProcessGitRunner"/> spawns a
/// child process.
/// </summary>
public interface IGitRunner
{
    /// <summary>The resolved git executable, for diagnostics. Never a shell.</summary>
    string Executable { get; }

    /// <summary>True when `git --version` succeeded. Probed once, cached.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct);

    Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct);
}

/// <summary>
/// Runs git as a direct child process. Never through `cmd /c`: an MCP host launches this
/// server with an environment we do not control, and a shell hop adds a quoting surface and
/// an orphaned-process risk for no benefit.
/// </summary>
public sealed class ProcessGitRunner : IGitRunner
{
    /// <summary>Escape hatch for the GOTCHA below: git may not be on PATH under an MCP host.</summary>
    public const string PathVariable = "INCIDENTTIMELINE_GIT_PATH";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly SemaphoreSlim probeGate = new(1, 1);
    private bool? available;

    public ProcessGitRunner(string? executable = null) =>
        this.Executable = executable
            ?? Environment.GetEnvironmentVariable(PathVariable)
            ?? "git";

    public string Executable { get; }

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (this.available is { } cached)
        {
            return cached;
        }

        await this.probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (this.available is { } raced)
            {
                return raced;
            }

            GitResult probe = await this.RunAsync(["--version"], ct).ConfigureAwait(false);
            this.available = probe.Ok;
            return probe.Ok;
        }
        finally
        {
            this.probeGate.Release();
        }
    }

    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(this.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
        };

        // Commit authors and subjects in this estate are not all ASCII. Without this git may
        // hand back the log in whatever i18n.commitEncoding the repo declares.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("i18n.logOutputEncoding=UTF-8");
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                return new GitResult(-1, string.Empty, $"Could not start '{this.Executable}'.");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The probe path lands here when git is absent. Value, not exception.
            return new GitResult(-1, string.Empty, ex.Message);
        }

        // git never needs input from us; close it immediately rather than leave it open and
        // unused. This matters far more than it looks: without RedirectStandardInput=true above,
        // every git child would inherit THIS PROCESS's own stdin — fatal when this process is an
        // MCP stdio server, whose stdin is the host's JSON-RPC pipe. 26 git children all holding
        // a handle to that pipe hung every tool call indefinitely; nothing in this method threw,
        // so nothing in a unit test (which never spawns a real process under a real host) could
        // have caught it. Found only by driving a real stdio session end-to-end (Phase 6 DoD).
        process.StandardInput.Close();

        try
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new GitResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Verification's cancellation case: Ctrl-C must leave no orphaned git children.
            KillQuietly(process);
            throw;
        }
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or the OS refused. Nothing useful to do while unwinding.
        }
    }
}
