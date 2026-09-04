namespace Aftermath.Sources;

using System.Globalization;
using Aftermath.Contracts;

/// <summary>
/// Release, commit and changelog evidence from the local clones. Offline, read-only, no
/// credential: this is the substitute for the GitLab API, which is unreachable from this
/// machine (verified BadRequest, Phase 0).
///
/// Four verified traps are encoded here; each has a named guard below:
///  1. Every tag in the estate is LIGHTWEIGHT, so %(taggerdate) is empty — see TagFormat.
///  2. There are essentially no merge commits, so "merged to master" is not a release
///     signal; the tag is. Nothing here parses a merge.
///  3. %cI carries three different offsets inside a single repo — see ParseCommitLine.
///  4. git may be absent from an MCP host's PATH — see the Skipped branch in CollectAsync.
/// </summary>
public sealed class GitReleaseSource : IEvidenceSource
{
    public const string SourceName = "git";

    /// <summary>
    /// GOTCHA (tags are lightweight): every tag in the estate reports objecttype = commit and
    /// a blank %(taggername), so %(taggerdate) yields an EMPTY STRING and a parser built on it
    /// finds zero releases while appearing to work. %(creatordate:unix) falls back to the
    /// commit date for lightweight tags and is unambiguous — seconds since the epoch, with no
    /// offset to misread. %(objecttype)/%(objectname) are carried so the event can state what
    /// the tag actually points at, which keeps this assumption visible in the output itself.
    /// </summary>
    private const string TagFormat =
        "%(refname:short)%09%(creatordate:unix)%09%(objecttype)%09%(objectname)%09%(subject)";

    private const string CommitFormat = "%H%x09%cI%x09%an%x09%s";

    private readonly IGitRunner git;
    private readonly IReadOnlyList<string> ticketPrefixes;

    public GitReleaseSource(IGitRunner git, IReadOnlyList<string>? ticketPrefixes = null)
    {
        this.git = git;
        this.ticketPrefixes = ticketPrefixes is { Count: > 0 } ? ticketPrefixes : TicketKeys.DefaultPrefixes;
    }

    public string Name => SourceName;

    public bool IsOffline => true;

    public async Task<SourceResult> CollectAsync(
        IncidentWindow window,
        IReadOnlyList<ServiceManifest> services,
        CancellationToken ct)
    {
        if (!await this.git.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            return SourceResult.Skipped(
                SourceName,
                $"git not found on PATH — set {ProcessGitRunner.PathVariable} to the git executable. "
                + "No release, commit or changelog evidence was collected.");
        }

        var events = new List<TimelineEvent>();
        var notWorkingCopies = new List<string>();
        int read = 0;

        foreach (ServiceManifest service in services)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<string>? tagLines = await this.ReadTagsAsync(service, ct).ConfigureAwait(false);
            if (tagLines is null)
            {
                // Not a working copy. Three of the workspace's 29 manifests are archive
                // extractions with no .git at all; that is a named gap, not a failure.
                notWorkingCopies.Add(service.Key);
                continue;
            }

            read++;
            IReadOnlyList<ChangelogEntry> changelog = ReadChangelog(service);
            var matchedVersions = new HashSet<string>(StringComparer.Ordinal);

            events.AddRange(this.ReleaseEvents(service, window, tagLines, changelog, matchedVersions));
            events.AddRange(this.ChangelogOnlyEvents(service, window, changelog, matchedVersions));
            events.AddRange(await this.CommitEventsAsync(service, window, ct).ConfigureAwait(false));
        }

        events.Sort(static (a, b) =>
        {
            int byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Provenance, b.Provenance);
        });

        return SourceResult.Ok(SourceName, events, BuildMessage(read, services.Count, notWorkingCopies, events));
    }

    private static string BuildMessage(
        int read,
        int total,
        IReadOnlyList<string> notWorkingCopies,
        IReadOnlyList<TimelineEvent> events)
    {
        int releases = events.Count(e => e.Kind == EventKind.Release);
        string message =
            $"Read {read} of {total} clones; {releases} release(s) and {events.Count - releases} commit(s) in window.";

        return notWorkingCopies.Count == 0
            ? message
            : message + " Not git working copies, so nothing was read from them: "
                + string.Join(", ", notWorkingCopies) + ".";
    }

    /// <summary>Null means "this path is not a git working copy" — distinct from "no tags".</summary>
    private async Task<IReadOnlyList<string>?> ReadTagsAsync(ServiceManifest service, CancellationToken ct)
    {
        GitResult result = await this.git.RunAsync(
            ["-C", service.RepoPath, "for-each-ref", "--sort=creatordate", $"--format={TagFormat}", "refs/tags"],
            ct).ConfigureAwait(false);

        return result.Ok ? SplitLines(result.StdOut) : null;
    }

    private IEnumerable<TimelineEvent> ReleaseEvents(
        ServiceManifest service,
        IncidentWindow window,
        IReadOnlyList<string> tagLines,
        IReadOnlyList<ChangelogEntry> changelog,
        HashSet<string> matchedVersions)
    {
        foreach (string line in tagLines)
        {
            string[] parts = line.Split('\t', 5);
            if (parts.Length < 4 ||
                !long.TryParse(parts[1], CultureInfo.InvariantCulture, out long unixSeconds))
            {
                continue;
            }

            DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToUniversalTime();
            if (!window.Contains(at))
            {
                continue;
            }

            string tag = parts[0];
            string version = ChangelogParser.NormaliseVersion(tag);
            ChangelogEntry? entry = changelog.FirstOrDefault(
                c => string.Equals(c.Version, version, StringComparison.Ordinal));
            if (entry is not null)
            {
                matchedVersions.Add(version);
            }

            yield return new TimelineEvent
            {
                At = at,
                Kind = EventKind.Release,
                Confidence = Confidence.Observed,
                Service = service.Key,
                Summary = $"Released {tag}",
                Provenance = $"{service.Key}@{tag}",
                Tickets = TicketKeys.Extract(entry?.Body, this.ticketPrefixes),
                Detail = ReleaseDetail(service, parts, entry),
            };
        }
    }

    private static string ReleaseDetail(ServiceManifest service, string[] parts, ChangelogEntry? entry)
    {
        string objectType = parts[2];
        string sha = ShortSha(parts[3]);
        string target = string.Equals(objectType, "commit", StringComparison.Ordinal)
            ? $"lightweight tag on commit {sha}"
            : $"{objectType} object {sha}";

        if (entry is null)
        {
            return target;
        }

        string headline = ChangelogParser.Headline(entry.Body);
        string anchor = $"{service.Key}/CHANGELOG.md:{entry.Line}";
        return headline.Length == 0 ? $"{target}; {anchor}" : $"{target}; {anchor} — {headline}";
    }

    /// <summary>
    /// A version the CHANGELOG records but no tag carries. Rare but real: Genesis-Ledger-
    /// WebService (4), Ledger-TicketServiceV2 (4) and tpi (5) have such versions, measured
    /// 2026-09-04. The heading gives a DATE and no time, so the instant is Reported, pinned to
    /// midnight UTC and caveated rather than invented.
    /// </summary>
    private IEnumerable<TimelineEvent> ChangelogOnlyEvents(
        ServiceManifest service,
        IncidentWindow window,
        IReadOnlyList<ChangelogEntry> changelog,
        HashSet<string> matchedVersions)
    {
        foreach (ChangelogEntry entry in changelog)
        {
            if (matchedVersions.Contains(entry.Version) || !DayOverlapsWindow(entry.Date, window))
            {
                continue;
            }

            yield return new TimelineEvent
            {
                At = entry.Date,
                Kind = EventKind.Release,
                Confidence = Confidence.Reported,
                Service = service.Key,
                Summary = $"CHANGELOG records release {entry.Version}",
                Provenance = $"{service.Key}/CHANGELOG.md:{entry.Line}",
                Caveat = "No tag carries this version, and the heading records the date "
                    + entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    + " with no time of day. Placed at midnight UTC; the real instant is "
                    + "somewhere in that day.",
                Tickets = TicketKeys.Extract(entry.Body, this.ticketPrefixes),
                Detail = ChangelogParser.Headline(entry.Body),
            };
        }
    }

    /// <summary>The heading names a day, not an instant, so any part of that day counts.</summary>
    private static bool DayOverlapsWindow(DateTimeOffset midnightUtc, IncidentWindow window) =>
        midnightUtc < window.ToUtc && midnightUtc.AddDays(1) > window.FromUtc;

    private async Task<IReadOnlyList<TimelineEvent>> CommitEventsAsync(
        ServiceManifest service,
        IncidentWindow window,
        CancellationToken ct)
    {
        // HEAD only, deliberately: an unmerged branch was never deployed, so it cannot have
        // caused the incident. Unix-epoch bounds avoid handing git a date string it may read
        // in local time. The in-process window check below is what actually decides membership.
        GitResult result = await this.git.RunAsync(
            [
                "-C", service.RepoPath, "log",
                $"--since=@{window.FromUtc.ToUnixTimeSeconds()}",
                $"--until=@{window.ToUtc.ToUnixTimeSeconds()}",
                $"--format={CommitFormat}",
            ],
            ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            return [];
        }

        var events = new List<TimelineEvent>();
        foreach (string line in SplitLines(result.StdOut))
        {
            if (this.ParseCommitLine(service, window, line) is { } commit)
            {
                events.Add(commit);
            }
        }

        return events;
    }

    /// <summary>
    /// GOTCHA (%cI offsets differ inside one repo): accountservice's last 200 commits carry
    /// Z (135), +01:00 (53) and +02:00 (12). DateTimeOffset.Parse handles all three;
    /// DateTime.Parse does not. The +02:00 entries are a contributor in CEST — legitimate,
    /// and must not be filtered out. Everything leaves this method in UTC (constraint 5).
    /// </summary>
    private TimelineEvent? ParseCommitLine(ServiceManifest service, IncidentWindow window, string line)
    {
        string[] parts = line.Split('\t', 4);
        if (parts.Length < 4)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                parts[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset committed))
        {
            return null;
        }

        DateTimeOffset at = committed.ToUniversalTime();
        if (!window.Contains(at))
        {
            return null;
        }

        string subject = parts[3].Trim();
        return new TimelineEvent
        {
            At = at,
            Kind = EventKind.Commit,
            Confidence = Confidence.Observed,
            Service = service.Key,
            Summary = subject.Length == 0 ? "(no commit subject)" : subject,
            Provenance = $"{service.Key}@{ShortSha(parts[0])}",
            Tickets = TicketKeys.Extract(subject, this.ticketPrefixes),
            Detail = $"author: {parts[2]}",
        };
    }

    /// <summary>
    /// Constraint 12: nothing in the evidence workspace is opened for writing, and
    /// FileShare.ReadWrite lets another process hold the file open while we read it.
    /// </summary>
    private static IReadOnlyList<ChangelogEntry> ReadChangelog(ServiceManifest service)
    {
        string path = Path.Combine(service.RepoPath, "CHANGELOG.md");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return ChangelogParser.Parse(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No changelog, or unreadable. Tags remain the authoritative release signal.
            return [];
        }
    }

    private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

    private static IReadOnlyList<string> SplitLines(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToArray();
}
