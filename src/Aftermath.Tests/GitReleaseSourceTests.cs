namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Sources;

public sealed class GitReleaseSourceTests
{
    private static readonly DateTimeOffset IncidentAt = new(2026, 7, 17, 13, 0, 0, TimeSpan.Zero);

    /// <summary>Verbatim output of the real command against core-service, 2026-09-04.</summary>
    private const string CoreServiceTags =
        "v1.13.0\t1783591891\tcommit\t1111111111111111111111111111111111111111\tchore(release): 1.13.0 [skip ci]\n"
        + "v1.14.0\t1784288900\tcommit\t273c45186c25026289808a7ea034c7c2a6379db6\tchore(release): 1.14.0 [skip ci]\n"
        + "v1.15.0\t1784290761\tcommit\t5b4c9eb24d78f80be1a98b241711258638b9e9d5\tchore(release): 1.15.0 [skip ci]\n";

    /// <summary>Verbatim, including all three offsets the estate mixes inside one repo.</summary>
    private const string CoreServiceLog =
        "5b4c9eb24d78f80be1a98b241711258638b9e9d5\t2026-07-17T13:19:21+01:00\tsemantic-release-bot\tchore(release): 1.15.0 [skip ci]\n"
        + "9a6ba53cfe144195b2d45873ef59212fd810ed61\t2026-07-17T11:56:01Z\tgilmar\tfeat: record virtual zone Id. Closes Jira ticket [INC-7337](https://acme.atlassian.net/browse/INC-7337)\n"
        + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\t2026-07-17T13:30:00+02:00\tcontributor-in-cest\tfix: PMOB-172 retry the token refresh\n";

    private static ServiceManifest Manifest(string key, string? repoPath = null) => new()
    {
        Key = key,
        RepoPath = repoPath ?? Path.Combine(Path.GetTempPath(), "no-such-clone", key),
    };

    private static IncidentWindow Window(TimeSpan? back = null, TimeSpan? forward = null) => new()
    {
        AtUtc = IncidentAt,
        LookBack = back ?? TimeSpan.FromHours(24),
        LookForward = forward ?? TimeSpan.FromHours(2),
    };

    private static FakeGitRunner CoreServiceRunner() => new(args =>
        FakeGitRunner.IsTagRead(args) ? new GitResult(0, CoreServiceTags, string.Empty)
        : FakeGitRunner.IsCommitRead(args) ? new GitResult(0, CoreServiceLog, string.Empty)
        : new GitResult(0, string.Empty, string.Empty));

    [Fact]
    public async Task Reads_lightweight_tags_via_creatordate_not_taggerdate()
    {
        // The regression guard for the estate's biggest git trap: every tag is lightweight,
        // %(taggerdate) is an empty string, and a source built on it finds zero releases while
        // exiting 0. Asserting on the format string catches the bug at its cause.
        FakeGitRunner git = CoreServiceRunner();

        await new GitReleaseSource(git).CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        string format = git.FormatArgumentOf(FakeGitRunner.IsTagRead);
        Assert.Contains("%(creatordate:unix)", format, StringComparison.Ordinal);
        Assert.DoesNotContain("taggerdate", format, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Finds_both_releases_around_the_incident_and_excludes_the_older_one()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        string[] releases = result.Events
            .Where(e => e.Kind == EventKind.Release)
            .Select(e => e.Provenance)
            .ToArray();

        Assert.Equal(new[] { "core-service@v1.14.0", "core-service@v1.15.0" }, releases);
    }

    [Fact]
    public async Task Release_instants_are_utc_and_match_the_epoch_seconds_git_reported()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        TimelineEvent v1150 = result.Events.Single(e => e.Provenance == "core-service@v1.15.0");

        // 1784290761 == 2026-07-17T12:19:21Z, which is 13:19:21 Dublin local — the wall-clock
        // time a human would quote. The event must carry the UTC instant, not the local one.
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 12, 19, 21, TimeSpan.Zero), v1150.At);
        Assert.Equal(TimeSpan.Zero, v1150.At.Offset);
        Assert.Equal(Confidence.Observed, v1150.Confidence);
    }

    [Fact]
    public async Task Every_event_carries_provenance_and_a_zero_offset()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        Assert.NotEmpty(result.Events);
        Assert.All(result.Events, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Provenance));
            Assert.Equal(TimeSpan.Zero, e.At.Offset);
        });
    }

    [Fact]
    public async Task Keeps_commits_written_at_every_offset_the_estate_uses()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        TimelineEvent[] commits = [.. result.Events.Where(e => e.Kind == EventKind.Commit)];

        // Z, +01:00 and +02:00 all survive; the CEST one is a real contributor, not corruption.
        Assert.Equal(3, commits.Length);
        Assert.Contains(commits, c => c.At == new DateTimeOffset(2026, 7, 17, 11, 30, 0, TimeSpan.Zero));
        Assert.Contains(commits, c => c.At == new DateTimeOffset(2026, 7, 17, 11, 56, 1, TimeSpan.Zero));
        Assert.Contains(commits, c => c.At == new DateTimeOffset(2026, 7, 17, 12, 19, 21, TimeSpan.Zero));
    }

    [Fact]
    public async Task Extracts_only_allow_listed_ticket_prefixes()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        Assert.Contains(result.Events, e => e.Tickets.Contains("INC-7337"));
        Assert.Contains(result.Events, e => e.Tickets.Contains("PMOB-172"));
    }

    [Fact]
    public async Task Skips_the_whole_source_when_git_is_absent()
    {
        var git = new FakeGitRunner(_ => new GitResult(-1, string.Empty, "not found"), available: false);

        SourceResult result = await new GitReleaseSource(git)
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Empty(result.Events);
        Assert.Contains(ProcessGitRunner.PathVariable, result.Message, StringComparison.Ordinal);
        Assert.Empty(git.Invocations);
    }

    [Fact]
    public async Task Names_directories_that_are_not_working_copies_instead_of_failing()
    {
        // Three of the workspace's 29 manifests are archive extractions with no .git.
        var git = new FakeGitRunner(args =>
            args.Contains("shared-master") ? FakeGitRunner.NotARepo()
            : FakeGitRunner.IsTagRead(args) ? new GitResult(0, CoreServiceTags, string.Empty)
            : new GitResult(0, string.Empty, string.Empty));

        SourceResult result = await new GitReleaseSource(git).CollectAsync(
            Window(),
            [Manifest("core-service"), Manifest("shared-master", "shared-master")],
            CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        Assert.Contains("Read 1 of 2 clones", result.Message, StringComparison.Ordinal);
        Assert.Contains("shared-master", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Events, e => e.Service == "core-service");
        Assert.DoesNotContain(result.Events, e => e.Service == "shared-master");
    }

    [Fact]
    public async Task Bounds_the_git_log_read_with_unix_seconds_from_the_window()
    {
        FakeGitRunner git = CoreServiceRunner();
        IncidentWindow window = Window();

        await new GitReleaseSource(git).CollectAsync(window, [Manifest("core-service")], CancellationToken.None);

        IReadOnlyList<string> log = git.Invocations.Single(FakeGitRunner.IsCommitRead);
        Assert.Contains($"--since=@{window.FromUtc.ToUnixTimeSeconds()}", log);
        Assert.Contains($"--until=@{window.ToUtc.ToUnixTimeSeconds()}", log);
    }

    [Fact]
    public async Task Stops_between_clones_when_cancelled()
    {
        // The year-wide run touches 26 clones and 78 git invocations; Ctrl-C must abandon it
        // at the next repo boundary rather than after the last one.
        using var cancellation = new CancellationTokenSource();
        var git = new FakeGitRunner(args =>
        {
            cancellation.Cancel();
            return FakeGitRunner.IsTagRead(args)
                ? new GitResult(0, CoreServiceTags, string.Empty)
                : new GitResult(0, string.Empty, string.Empty);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GitReleaseSource(git)
            .CollectAsync(
                Window(),
                [Manifest("core-service"), Manifest("accountservice"), Manifest("billing-service")],
                cancellation.Token));

        Assert.DoesNotContain(git.Invocations, a => a.Contains("billing-service"));
    }

    [Fact]
    public async Task Events_are_ordered_by_instant()
    {
        SourceResult result = await new GitReleaseSource(CoreServiceRunner())
            .CollectAsync(Window(), [Manifest("core-service")], CancellationToken.None);

        DateTimeOffset[] instants = [.. result.Events.Select(e => e.At)];
        Assert.Equal(instants.Order(), instants);
    }

    [Fact]
    public void Source_is_offline_so_the_default_run_includes_it()
    {
        var source = new GitReleaseSource(CoreServiceRunner());

        Assert.True(source.IsOffline);
        Assert.Equal("git", source.Name);
    }
}
