namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Logs;
using Aftermath.Sources;

public sealed class LogFileSourceTests
{
    /// <summary>Matches every fixture line under Fixtures/logs/{rollover,dst}: time, session,
    /// thread, level, logger, message — pattern index 13 of the real estate's 22.</summary>
    private const string FixturePattern =
        "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] [%4thread] %-5level %logger - %message%newline";

    private static string FixturesRoot(string scenario) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "logs", scenario);

    private static ServiceManifest FixtureService(string key = "fixture-service") => new()
    {
        Key = key,
        RepoPath = Path.Combine(Path.GetTempPath(), "no-such-clone", key),
        PackageName = "Acme.Ledger.FixtureService.WebApi",
        LogPatterns = [FixturePattern],
    };

    [Fact]
    public async Task Skipped_when_no_log_root_supplied()
    {
        var source = new LogFileSource(logRoot: null);

        SourceResult result = await source.CollectAsync(
            IncidentWindow.Default(DateTimeOffset.UtcNow), [FixtureService()], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
        Assert.Contains("--log-root", result.Message, StringComparison.Ordinal);
        Assert.Empty(result.Events);
    }

    [Fact]
    public async Task Skipped_when_log_root_does_not_exist()
    {
        var source = new LogFileSource(Path.Combine(Path.GetTempPath(), "no-such-log-root-" + Guid.NewGuid()));

        SourceResult result = await source.CollectAsync(
            IncidentWindow.Default(DateTimeOffset.UtcNow), [FixtureService()], CancellationToken.None);

        Assert.Equal(SourceStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task Midnight_rollover_events_are_inferred_and_caveated_past_the_carry()
    {
        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 9, 3, 23, 0, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(2),
            LookForward = TimeSpan.FromHours(3),
        };

        SourceResult result = await new LogFileSource(FixturesRoot("rollover"))
            .CollectAsync(window, [FixtureService()], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
        TimelineEvent[] logEvents = [.. result.Events.Where(e => e.Kind is EventKind.LogWarning or EventKind.LogError)];

        // Before the carry: 23:59:30 IST 9/3 == 22:59:30Z, Observed, no rollover caveat.
        TimelineEvent beforeCarry = Assert.Single(logEvents, e => e.Summary.Contains("low disk space"));
        Assert.Equal(Confidence.Observed, beforeCarry.Confidence);
        Assert.Null(beforeCarry.Caveat);

        // After the carry: 00:05:00 local now resolves to 9/4, not 9/3.
        TimelineEvent afterCarry = Assert.Single(logEvents, e => e.Summary.Contains("midnight batch failed"));
        Assert.Equal(Confidence.Inferred, afterCarry.Confidence);
        Assert.Contains("date advanced by midnight rollover", afterCarry.Caveat, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 23, 5, 0, TimeSpan.Zero), afterCarry.At);
    }

    [Fact]
    public async Task Rolled_dot_one_file_is_read_before_the_current_file_by_write_time_not_name()
    {
        // git checkout does not preserve mtimes, so the checked-in fixture's on-disk write
        // order is not deterministic across clones/CI — exactly why the plan insists on
        // sorting by LastWriteTime rather than trusting name order (".10" < ".2" lexically).
        // Set both explicitly here so the ".1" file is unambiguously the OLDER, already-rolled
        // segment, and prove it is folded into the same LogFileClock as the current file
        // rather than read after it (which would wrongly see its 22:00 line as "the next day").
        string rolloverDir = FixturesRoot("rollover");
        DateTime now = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(Path.Combine(rolloverDir, "Acme.Ledger.FixtureService.WebApi_2026-09-03.log.1"), now.AddHours(-1));
        File.SetLastWriteTimeUtc(Path.Combine(rolloverDir, "Acme.Ledger.FixtureService.WebApi_2026-09-03.log"), now);

        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 9, 3, 23, 0, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(2),
            LookForward = TimeSpan.FromHours(3),
        };

        SourceResult result = await new LogFileSource(rolloverDir)
            .CollectAsync(window, [FixtureService()], CancellationToken.None);

        TimelineEvent rolled = Assert.Single(result.Events, e => e.Summary.Contains("earlier rolled segment failure"));
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 21, 0, 0, TimeSpan.Zero), rolled.At); // 22:00 IST == 21:00Z
        Assert.Equal(Confidence.Observed, rolled.Confidence);
    }

    [Fact]
    public async Task Ambiguous_DST_line_is_inferred_with_the_overlap_caveat()
    {
        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(2),
            LookForward = TimeSpan.FromHours(1),
        };

        SourceResult result = await new LogFileSource(FixturesRoot("dst"))
            .CollectAsync(window, [FixtureService()], CancellationToken.None);

        TimelineEvent ambiguous = Assert.Single(result.Events, e => e.Summary.Contains("ambiguous instant"));
        Assert.Equal(Confidence.Inferred, ambiguous.Confidence);
        Assert.Contains("ambiguous local time (DST overlap); assumed first pass", ambiguous.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_DST_line_is_inferred_and_shifted()
    {
        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(2),
            LookForward = TimeSpan.FromHours(1),
        };

        SourceResult result = await new LogFileSource(FixturesRoot("dst"))
            .CollectAsync(window, [FixtureService()], CancellationToken.None);

        TimelineEvent invalid = Assert.Single(result.Events, e => e.Summary.Contains("invalid instant"));
        Assert.Equal(Confidence.Inferred, invalid.Confidence);
        Assert.Contains("local time does not exist (DST gap); shifted forward one hour", invalid.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_IOException_when_the_fixture_file_is_held_open_by_a_second_handle()
    {
        // Constraint 12 / §3.4: a running service holds its own log file open.
        string path = Path.Combine(FixturesRoot("rollover"), "Acme.Ledger.FixtureService.WebApi_2026-09-03.log");
        using var heldOpen = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 9, 3, 23, 0, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(2),
            LookForward = TimeSpan.FromHours(3),
        };

        SourceResult result = await new LogFileSource(FixturesRoot("rollover"))
            .CollectAsync(window, [FixtureService()], CancellationToken.None);

        Assert.Equal(SourceStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Http_response_lines_yield_per_minute_metrics_with_p50_and_p95()
    {
        string dir = Path.Combine(Path.GetTempPath(), "incidenttimeline-http-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "Acme.Ledger.FixtureService.WebApi_2026-09-03.log");
            using (var writer = new StreamWriter(path))
            {
                // Seconds increase monotonically across all 200 lines (never wrapping back to
                // 0) so the fixture cannot accidentally trip LogFileClock's midnight-rollover
                // detector, which reads any decrease in time-of-day as the day having advanced.
                for (int i = 0; i < 200; i++)
                {
                    TimeOnly time = new TimeOnly(12, 0, 0).Add(TimeSpan.FromSeconds(i));
                    int status = i % 50 == 0 ? 500 : i % 10 == 0 ? 404 : 200;
                    int elapsedMs = 100 + i;
                    writer.WriteLine(
                        $"{time:HH:mm:ss.fff} [sess-1] [0001] INFO  Worker - HTTP Response: GET /api/v1/Ping "
                        + $"| StatusCode: {status} | Headers: {{}} | Body:  | ContentType: application/json | ElapsedMs: {elapsedMs}");
                }
            }

            var window = new IncidentWindow
            {
                AtUtc = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
                LookBack = TimeSpan.FromHours(2),
                LookForward = TimeSpan.FromHours(2),
            };

            SourceResult result = await new LogFileSource(dir).CollectAsync(window, [FixtureService()], CancellationToken.None);

            TimelineEvent[] metrics = [.. result.Events.Where(e => e.Kind == EventKind.HttpMetrics)];
            Assert.NotEmpty(metrics);
            Assert.All(metrics, e => Assert.Equal(Confidence.Observed, e.Confidence));

            int totalRequests = metrics.Sum(e => int.Parse(e.Detail!.Split("requests=")[1].Split(' ')[0]));
            Assert.Equal(200, totalRequests);
            Assert.Contains(metrics, e => e.Detail!.Contains("4xx=") && !e.Detail.Contains("4xx=0"));
            Assert.Contains(metrics, e => e.Detail!.Contains("5xx=") && !e.Detail.Contains("5xx=0"));
            Assert.Contains(metrics, e => e.Detail!.Contains("p95Ms="));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Accountservice_reports_http_metrics_unavailable_instead_of_zero()
    {
        string dir = Path.Combine(Path.GetTempPath(), "incidenttimeline-http-unavail-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var manifest = new ServiceManifest
            {
                Key = "accountservice",
                RepoPath = Path.Combine(Path.GetTempPath(), "no-such-clone", "accountservice"),
                PackageName = "Acme.Ledger.AccountService.WebApi",
                LogPatterns = [FixturePattern],
            };

            string path = Path.Combine(dir, "Acme.Ledger.AccountService.WebApi_2026-09-03.log");
            File.WriteAllText(path, "12:00:00.000 [sess-1] [0001] INFO  Worker - HTTP Response: GET / | StatusCode: 200 | ElapsedMs: 5\n");

            var window = new IncidentWindow
            {
                AtUtc = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
                LookBack = TimeSpan.FromHours(2),
                LookForward = TimeSpan.FromHours(2),
            };

            SourceResult result = await new LogFileSource(dir).CollectAsync(window, [manifest], CancellationToken.None);

            TimelineEvent unavailable = Assert.Single(result.Events, e => e.Kind == EventKind.HttpMetrics);
            Assert.Equal("HTTP metrics unavailable for this service", unavailable.Summary);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Error_txt_worker_style_yields_two_stacked_crashes()
    {
        var manifest = new ServiceManifest
        {
            Key = "gdpr-service",
            RepoPath = Path.Combine(Path.GetTempPath(), "no-such-clone", "gdpr-service"),
            PackageName = "Acme.Ledger.GdprService.Worker",
            LogPatterns = [],
        };

        var window = new IncidentWindow
        {
            AtUtc = new DateTimeOffset(2026, 9, 3, 16, 0, 0, TimeSpan.Zero),
            LookBack = TimeSpan.FromHours(6),
            LookForward = TimeSpan.FromHours(6),
        };

        SourceResult result = await new LogFileSource(FixturesRoot("errortxt/worker"))
            .CollectAsync(window, [manifest], CancellationToken.None);

        TimelineEvent[] crashes = [.. result.Events.Where(e => e.Kind == EventKind.ServiceStop)];
        Assert.Equal(2, crashes.Length);
        Assert.All(crashes, e => Assert.Equal(Confidence.Observed, e.Confidence));
        Assert.All(crashes, e => Assert.Equal("gdpr-service", e.Service));
    }

    [Fact]
    public async Task Error_txt_shared_host_style_yields_one_event_dated_from_last_write_time()
    {
        var manifest = new ServiceManifest
        {
            Key = "some-host",
            RepoPath = Path.Combine(Path.GetTempPath(), "no-such-clone", "some-host"),
            PackageName = "Acme.Ledger.SomeHost.WebApi",
            LogPatterns = [],
        };

        string path = Path.Combine(FixturesRoot("errortxt/host"), "Acme.Ledger.SomeHost.WebApi", "Error.txt");
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);

        var window = new IncidentWindow { AtUtc = new DateTimeOffset(lastWrite, TimeSpan.Zero), LookBack = TimeSpan.FromDays(3650), LookForward = TimeSpan.FromDays(1) };

        SourceResult result = await new LogFileSource(FixturesRoot("errortxt/host"))
            .CollectAsync(window, [manifest], CancellationToken.None);

        TimelineEvent crash = Assert.Single(result.Events, e => e.Kind == EventKind.ServiceStop);
        Assert.Equal(Confidence.Inferred, crash.Confidence);
        Assert.Contains("last-write time", crash.Caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_is_offline()
    {
        Assert.True(new LogFileSource(null).IsOffline);
        Assert.Equal("logs", new LogFileSource(null).Name);
    }
}
