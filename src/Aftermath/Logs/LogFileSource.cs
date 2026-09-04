namespace Aftermath.Logs;

using System.Globalization;
using Aftermath.Contracts;
using Aftermath.Sources;

/// <summary>
/// Reads log4net rolling files and Error.txt crash dumps from a locally-copied log root.
/// Offline, read-only (constraint 12). Production's log directory can never be known from a
/// clone — every Release config resolves it through the Octopus token
/// "#{Acme.Logs.Path.And.File.Prefix}" — so this source is Skipped outright when
/// --log-root is not supplied (§3.5): there is nothing to guess at.
/// </summary>
public sealed class LogFileSource : IEvidenceSource
{
    public const string SourceName = "logs";

    /// <summary>accountservice and billing-service use RequestLoggingMiddleware from the
    /// Acme.Ledger.Shared NuGet package, whose source is not in the workspace — its
    /// line format is unknown, so HTTP metrics cannot be extracted for either (§3.6a).</summary>
    private static readonly string[] UnknownHttpMiddlewareServices = ["accountservice", "billing-service"];

    private readonly string? logRoot;
    private readonly TimeZoneInfo zone;

    public LogFileSource(string? logRoot, TimeZoneInfo? zone = null)
    {
        this.logRoot = logRoot;
        this.zone = zone ?? LogFileClock.DefaultZone;
    }

    public string Name => SourceName;

    public bool IsOffline => true;

    public Task<SourceResult> CollectAsync(IncidentWindow window, IReadOnlyList<ServiceManifest> services, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(this.logRoot))
        {
            return Task.FromResult(SourceResult.Skipped(
                SourceName,
                "no --log-root supplied; log evidence not collected. Copy the service's logs\\ "
                + "directory locally and pass --log-root."));
        }

        if (!Directory.Exists(this.logRoot))
        {
            return Task.FromResult(SourceResult.Skipped(SourceName, $"--log-root '{this.logRoot}' does not exist."));
        }

        var events = new List<TimelineEvent>();
        var noPackageName = new List<string>();
        var noFilesFound = new List<string>();
        int matched = 0;

        foreach (ServiceManifest service in services)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(service.PackageName))
            {
                noPackageName.Add(service.Key);
                continue;
            }

            IReadOnlyList<string> files = FindLogFiles(this.logRoot, service.PackageName);
            if (files.Count == 0)
            {
                noFilesFound.Add(service.Key);
                continue;
            }

            matched++;
            events.AddRange(this.ReadService(service, files, window));
        }

        events.AddRange(ErrorTxtReader.Read(this.logRoot, services, window, this.zone));

        events.Sort(static (a, b) =>
        {
            int byTime = a.At.CompareTo(b.At);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Provenance, b.Provenance);
        });

        return Task.FromResult(SourceResult.Ok(SourceName, events, BuildMessage(matched, services.Count, noPackageName, noFilesFound)));
    }

    private static string BuildMessage(int matched, int total, IReadOnlyList<string> noPackageName, IReadOnlyList<string> noFilesFound)
    {
        string message = $"Read logs for {matched} of {total} services.";
        if (noPackageName.Count > 0)
        {
            message += " No package name to match a log prefix: " + string.Join(", ", noPackageName) + ".";
        }

        if (noFilesFound.Count > 0)
        {
            message += " No log files found under --log-root: " + string.Join(", ", noFilesFound) + ".";
        }

        return message;
    }

    /// <summary>
    /// Files are matched by the "&lt;PackageName&gt;_" filename prefix (§3.5). Searching
    /// recursively under --log-root, rather than only its top level, satisfies both resolution
    /// paths the plan describes — a flat copy of files directly into --log-root, or a copy that
    /// preserved the dev config's relative "logs\" subfolder — without two code paths.
    /// </summary>
    private static IReadOnlyList<string> FindLogFiles(string logRoot, string packageName)
    {
        try
        {
            return [.. Directory.EnumerateFiles(logRoot, $"{packageName}_*.log*", SearchOption.AllDirectories)];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IEnumerable<TimelineEvent> ReadService(ServiceManifest service, IReadOnlyList<string> files, IncidentWindow window)
    {
        SortedDictionary<DateOnly, List<string>> groups = this.GroupByFilenameDate(files, window);
        var rawLines = new List<RawLogLine>();
        var http = new HttpMetricsAccumulator();
        bool httpUnavailable = UnknownHttpMiddlewareServices.Contains(service.Key, StringComparer.OrdinalIgnoreCase);
        var chooser = new PatternChooser(service.LogPatterns);

        foreach ((DateOnly filenameDate, List<string> group) in groups)
        {
            var clock = new LogFileClock(filenameDate, this.zone);
            foreach (string file in group)
            {
                this.ReadFile(service, file, clock, chooser, http, httpUnavailable, window, rawLines);
            }
        }

        List<TimelineEvent> events = [.. LogClusterer.Cluster(service.Key, rawLines)];
        events.AddRange(httpUnavailable ? [HttpMetrics.Unavailable(service.Key, window.AtUtc)] : http.BuildEvents(service.Key));
        return events;
    }

    /// <summary>Parses every line of one file into a <see cref="RawLogLine"/> candidate for
    /// clustering (§3.7) — raw error lines are noise, so nothing here becomes a TimelineEvent
    /// directly; <see cref="LogClusterer"/> does that once the whole service has been read.</summary>
    private void ReadFile(
        ServiceManifest service,
        string file,
        LogFileClock clock,
        PatternChooser chooser,
        HttpMetricsAccumulator http,
        bool httpUnavailable,
        IncidentWindow window,
        List<RawLogLine> rawLines)
    {
        foreach ((int lineNumber, string rawLine) in ReadLines(file))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            if (!chooser.TryParse(rawLine, out LogLineFields fields, out bool usedFallback))
            {
                continue;
            }

            if (!this.TryResolveInstant(fields, clock, out DateTimeOffset at, out Confidence confidence, out string? caveat) ||
                !window.Contains(at))
            {
                continue;
            }

            if (!httpUnavailable && fields.Message.StartsWith("HTTP Response:", StringComparison.Ordinal))
            {
                http.Add(at, fields.Message);
                continue;
            }

            if (!IsWarningOrWorse(fields.Level, out EventKind kind))
            {
                continue;
            }

            if (usedFallback)
            {
                confidence = Confidence.Inferred;
                caveat = caveat is null ? "log pattern not fully recognised" : $"{caveat}; log pattern not fully recognised";
            }
            else if (chooser.HadMultipleCandidates)
            {
                string note = $"log pattern chosen from {service.LogPatterns.Count} candidates for this service";
                caveat = caveat is null ? note : $"{caveat}; {note}";
            }

            rawLines.Add(BuildRawLine(file, lineNumber, at, confidence, caveat, kind, fields));
        }
    }

    /// <summary>
    /// GOTCHA: the two patterns that use bare %date (no format) already carry a full local
    /// timestamp, so they skip LogFileClock's filename-date/rollover machinery entirely.
    /// DateTime.ParseExact (not DateTimeOffset) is deliberate — see ErrorTxtReader.ParseDate
    /// for the same reasoning: the string carries no offset, so it must be read as local time
    /// in the CONFIGURED zone, then converted immediately.
    /// </summary>
    private bool TryResolveInstant(
        LogLineFields fields, LogFileClock clock, out DateTimeOffset at, out Confidence confidence, out string? caveat)
    {
        const string fullFormat = "yyyy-MM-dd HH:mm:ss,fff";
        if (fields.FullLocalDateTime is { } full)
        {
            if (DateTime.TryParseExact(full, fullFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime naive)) // -> DateTimeOffset
            {
                (at, confidence, caveat) = LogFileClock.ToUtc(naive, this.zone);
                return true;
            }
        }
        else if (fields.TimeOfDay is { } timeText &&
                 TimeOnly.TryParseExact(timeText, "HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly timeOfDay))
        {
            (at, confidence, caveat) = clock.Resolve(timeOfDay);
            return true;
        }

        at = default;
        confidence = default;
        caveat = null;
        return false;
    }

    private static bool IsWarningOrWorse(string? level, out EventKind kind)
    {
        switch (level?.ToUpperInvariant())
        {
            case "WARN":
            case "WARNING":
                kind = EventKind.LogWarning;
                return true;
            case "ERROR":
                kind = EventKind.LogError;
                return true;
            case "FATAL":
                kind = EventKind.LogFatal;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    private static RawLogLine BuildRawLine(
        string file,
        int lineNumber,
        DateTimeOffset at,
        Confidence confidence,
        string? caveat,
        EventKind kind,
        LogLineFields fields) =>
        new()
        {
            At = at,
            Kind = kind,
            Confidence = confidence,
            Caveat = caveat,
            Message = fields.Message.Length > 200 ? fields.Message[..200] + "…" : fields.Message,
            Provenance = $"{Path.GetFileName(file)}:{lineNumber}",
            TraceId = fields.TraceId,
            SpanId = fields.SpanId,
            CorrelationPrefix = fields.Correlation ?? fields.LeadingBracketSegment,
            Logger = fields.Logger,
        };

    /// <summary>
    /// The window's UTC bounds converted into the configured zone's calendar dates, widened by
    /// one day at the start: a file named for day X can hold day X+1's early hours too (§3.2),
    /// so a window starting just after local midnight could otherwise miss the file that
    /// actually carries it.
    /// </summary>
    private SortedDictionary<DateOnly, List<string>> GroupByFilenameDate(IReadOnlyList<string> files, IncidentWindow window)
    {
        // DateOnly.FromDateTime has no DateTimeOffset overload, so .DateTime below extracts the
        // local wall-clock component of an already-converted DateTimeOffset purely to drop its
        // time part — the offset itself was already applied by ConvertTime.
        DateOnly localFrom = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.FromUtc, this.zone).DateTime).AddDays(-1); // DateTimeOffset -> DateOnly
        DateOnly localTo = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(window.ToUtc, this.zone).DateTime); // DateTimeOffset -> DateOnly

        var groups = new SortedDictionary<DateOnly, List<string>>();
        foreach (string file in files)
        {
            if (LogFileName.TryReadDate(Path.GetFileName(file)) is not { } date || date < localFrom || date > localTo)
            {
                continue;
            }

            if (!groups.TryGetValue(date, out List<string>? list))
            {
                list = [];
                groups[date] = list;
            }

            list.Add(file);
        }

        // .10 sorts before .2 lexically (§3.2) — order the rolled parts of one filename-date by
        // when the appender actually finished writing them, not by name.
        foreach (List<string> list in groups.Values)
        {
            list.Sort((a, b) => File.GetLastWriteTimeUtc(a).CompareTo(File.GetLastWriteTimeUtc(b)));
        }

        return groups;
    }

    /// <summary>Constraint 12: FileShare.ReadWrite — a running service holds its current log
    /// file open, and opening without it throws IOException during exactly the incident this
    /// tool exists to help with.</summary>
    private static IEnumerable<(int LineNumber, string Text)> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        int lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            yield return (lineNumber, line);
        }
    }

    /// <summary>
    /// GOTCHA (one service, several line formats — pick per file, not per service): tries each
    /// of the service's candidate patterns against the first non-blank line it sees and keeps
    /// the winner for the rest of that service's files, since api-gateway2's file and syslog
    /// appenders order the same fields differently (§3.1). A line the winner cannot match still
    /// gets a second attempt through FallbackLogParser rather than being dropped outright — a
    /// stack-trace continuation line has no timestamp of its own either way.
    /// </summary>
    private sealed class PatternChooser
    {
        private readonly IReadOnlyList<string> candidates;
        private Log4NetPatternParser.CompiledLogPattern? winner;
        private bool chosen;

        public PatternChooser(IReadOnlyList<string> candidates) => this.candidates = candidates;

        public bool HadMultipleCandidates { get; private set; }

        public bool TryParse(string line, out LogLineFields fields, out bool usedFallback)
        {
            if (!this.chosen)
            {
                this.Choose(line);
            }

            if (this.winner is not null && this.winner.TryMatch(line, out fields))
            {
                usedFallback = false;
                return true;
            }

            usedFallback = true;
            return FallbackLogParser.TryMatch(line, out fields);
        }

        private void Choose(string firstLine)
        {
            this.chosen = true;
            int compilable = 0;
            foreach (string pattern in this.candidates)
            {
                Log4NetPatternParser.CompiledLogPattern? compiled = Log4NetPatternParser.Compile(pattern);
                if (compiled is null)
                {
                    continue;
                }

                compilable++;
                if (this.winner is null && compiled.TryMatch(firstLine, out _))
                {
                    this.winner = compiled;
                }
            }

            this.HadMultipleCandidates = compilable > 1;
        }
    }
}
