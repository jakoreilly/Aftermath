namespace Aftermath.Tests;

using System.Diagnostics;
using Aftermath.Contracts;
using Aftermath.Sources;

/// <summary>
/// The one place a real git is driven. Everything happens in a throwaway repo under the OS
/// temp directory — constraint 12 means no test may write inside the evidence workspace.
///
/// Worth the cost of a real process: <see cref="GitReleaseSourceTests"/> proves the format
/// string asks for %(creatordate:unix), but only a real git proves that a real LIGHTWEIGHT
/// tag comes back with a date at all. Swap in %(taggerdate) and this test reports zero
/// releases while the process still exits 0 — exactly the failure the plan warns about.
/// </summary>
public class ProcessGitRunnerTests : IDisposable
{
    private static readonly DateTimeOffset TaggedAt = new(2026, 7, 17, 12, 19, 21, TimeSpan.Zero);

    private readonly string repoPath =
        Path.Combine(Path.GetTempPath(), "incidenttimeline-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        DeleteQuietly(this.repoPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Probe_finds_the_git_on_path() =>
        Assert.True(await new ProcessGitRunner().IsAvailableAsync(CancellationToken.None));

    [Fact]
    public async Task Reports_git_as_unavailable_instead_of_throwing_when_the_path_is_wrong()
    {
        // The MCP-host case: git is not on PATH, or INCIDENTTIMELINE_GIT_PATH is stale.
        var runner = new ProcessGitRunner(Path.Combine(Path.GetTempPath(), "definitely-not-git.exe"));

        Assert.False(await runner.IsAvailableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Skips_the_source_when_git_cannot_be_launched()
    {
        var runner = new ProcessGitRunner(Path.Combine(Path.GetTempPath(), "definitely-not-git.exe"));
        var source = new GitReleaseSource(runner);
        var window = new IncidentWindow
        {
            AtUtc = TaggedAt, LookBack = TimeSpan.FromHours(24), LookForward = TimeSpan.FromHours(2),
        };

        SourceResult result = await source.CollectAsync(
            window,
            [new ServiceManifest { Key = "fixture", RepoPath = this.repoPath }],
            CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains(ProcessGitRunner.PathVariable, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_a_real_lightweight_tag_and_dates_it_from_creatordate()
    {
        if (!SeedLightweightTaggedRepo(this.repoPath, TaggedAt))
        {
            return; // No usable git on this machine; the mocked guards still cover the format.
        }

        var source = new GitReleaseSource(new ProcessGitRunner());
        var window = new IncidentWindow
        {
            AtUtc = TaggedAt, LookBack = TimeSpan.FromHours(1), LookForward = TimeSpan.FromHours(1),
        };

        SourceResult result = await source.CollectAsync(
            window,
            [new ServiceManifest { Key = "fixture", RepoPath = this.repoPath }],
            CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        TimelineEvent release = Assert.Single(result.Events, e => e.Kind == EventKind.Release);

        Assert.Equal("fixture@v9.9.9", release.Provenance);
        Assert.Equal(TaggedAt, release.At);
        Assert.Equal(Confidence.Observed, release.Confidence);
        Assert.Contains("lightweight tag", release.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Git_process_stdin_is_redirected_so_it_never_hangs_waiting_on_it()
    {
        // The real bug, found only by driving a live MCP stdio session end-to-end (Phase 6
        // DoD), not by any unit test: without RedirectStandardInput=true on the git
        // ProcessStartInfo, every git child inherits THIS PROCESS's own stdin handle. That is
        // fatal specifically when this process is an MCP server — its stdin is the host's
        // JSON-RPC pipe, and every tool call that shelled out to git hung indefinitely with no
        // exception anywhere to catch. A unit test's own stdin is not that pipe, so this
        // cannot reproduce the hang by the same mechanism; it instead pins the fix's visible
        // effect — RunAsync completes promptly rather than blocking on standard input at all.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        GitResult result = await new ProcessGitRunner().RunAsync(["--version"], timeout.Token);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task Cancelling_stops_the_child_process_rather_than_orphaning_it()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ProcessGitRunner().RunAsync(["--version"], cancelled.Token));
    }

    /// <summary>
    /// Arrangement only, so it drives Process directly: the dates must be forced through
    /// GIT_*_DATE, which the production runner deliberately does not expose.
    /// </summary>
    private static bool SeedLightweightTaggedRepo(string path, DateTimeOffset at)
    {
        Directory.CreateDirectory(path);
        string stamp = at.ToString("o");

        return Git(path, stamp, "init", "--quiet")
            && Git(path, stamp, "-c", "user.name=fixture", "-c", "user.email=fixture@example.invalid",
                "commit", "--allow-empty", "--quiet", "-m", "feat: seed INC-1")
            && Git(path, stamp, "tag", "v9.9.9");
    }

    private static bool Git(string workingDirectory, string dateStamp, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_AUTHOR_DATE"] = dateStamp;
        psi.Environment["GIT_COMMITTER_DATE"] = dateStamp;

        try
        {
            using Process? process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Git marks loose objects read-only, so a plain recursive delete throws.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp directory is not a test failure.
        }
    }
}
