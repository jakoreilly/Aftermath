namespace Aftermath.Logs;

using System.Globalization;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Error.txt is the only record of a startup failure: every host writes an unhandled startup
/// exception to a flat file beside the binary, bypassing log4net entirely, because the logging
/// pipeline itself never came up (plan.md §3.6b). The two writer styles change what a missing
/// entry MEANS — gdpr-service and transaction-import APPEND (a history of crashes survives);
/// the shared Windows/Linux hosts OVERWRITE (the file holds only the most recent one).
/// </summary>
public static partial class ErrorTxtReader
{
    // Workers write "Date : " + DateTime.Now — local, culture-formatted, no offset at all.
    [GeneratedRegex(@"^Date\s*:\s*(?<date>.+?)\s*$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DateLine();

    public static IEnumerable<TimelineEvent> Read(
        string logRoot, IReadOnlyList<ServiceManifest> services, IncidentWindow window, TimeZoneInfo zone)
    {
        foreach (string path in SafeEnumerate(logRoot))
        {
            if (!TryReadFile(path, out string content, out DateTimeOffset lastWriteUtc))
            {
                continue;
            }

            string serviceKey = MatchService(path, services);
            foreach (TimelineEvent evt in ParseFile(path, serviceKey, content, lastWriteUtc, zone, window))
            {
                yield return evt;
            }
        }
    }

    /// <summary>Constraint 12: FileShare.ReadWrite — the shared hosts hold their own Error.txt
    /// open while (over)writing it.</summary>
    private static bool TryReadFile(string path, out string content, out DateTimeOffset lastWriteUtc)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
            lastWriteUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            content = string.Empty;
            lastWriteUtc = default;
            return false;
        }
    }

    private static IEnumerable<string> SafeEnumerate(string logRoot)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(logRoot, "Error.txt", SearchOption.AllDirectories);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            yield return file;
        }
    }

    /// <summary>No structural link exists between an Error.txt and a service manifest — the
    /// crash happened before the app told anyone its identity. Matching on the package name
    /// appearing in the file's own path is the only signal a locally-copied log root offers.</summary>
    private static string MatchService(string path, IReadOnlyList<ServiceManifest> services)
    {
        foreach (ServiceManifest service in services)
        {
            if (service.PackageName is { Length: > 0 } name && path.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return service.Key;
            }
        }

        return "*";
    }

    private static IEnumerable<TimelineEvent> ParseFile(
        string path, string serviceKey, string content, DateTimeOffset lastWriteUtc, TimeZoneInfo zone, IncidentWindow window)
    {
        MatchCollection dateLines = DateLine().Matches(content);
        if (dateLines.Count == 0)
        {
            // Shared-host style: overwritten, no date recorded at all.
            if (window.Contains(lastWriteUtc))
            {
                yield return BuildEvent(
                    path, serviceKey, lastWriteUtc, Confidence.Inferred,
                    "no date recorded in Error.txt; dated from the file's last-write time", lineNumber: 1);
            }

            yield break;
        }

        // Worker style: appended, one "Date : …" line starting each stacked crash.
        foreach (Match m in dateLines)
        {
            (DateTimeOffset at, Confidence confidence, string? caveat) = ParseDate(m.Groups["date"].Value, lastWriteUtc, zone);
            if (window.Contains(at))
            {
                yield return BuildEvent(path, serviceKey, at, confidence, caveat, LineNumberAt(content, m.Index));
            }
        }
    }

    /// <summary>
    /// GOTCHA: the workers write "Date : " + DateTime.Now — local, culture-formatted, no
    /// offset. DateTime.TryParse (not DateTimeOffset.TryParse) is deliberate: a string with no
    /// offset must be read as local wall-clock time IN THE CONFIGURED ZONE, which is what
    /// LogFileClock.ToUtc does next — DateTimeOffset.TryParse would instead silently assume the
    /// HOST machine's OS time zone, which is exactly the kind of unlabelled-local-time bug
    /// constraint 5 exists to prevent.
    /// </summary>
    private static (DateTimeOffset At, Confidence Confidence, string? Caveat) ParseDate(string text, DateTimeOffset lastWriteUtc, TimeZoneInfo zone)
    {
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime invariantParse)) // -> DateTimeOffset
        {
            return LogFileClock.ToUtc(invariantParse, zone);
        }

        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out DateTime cultureParse)) // -> DateTimeOffset
        {
            return LogFileClock.ToUtc(cultureParse, zone);
        }

        return (lastWriteUtc, Confidence.Inferred, "Error.txt date could not be parsed; dated from the file's last-write time instead");
    }

    private static TimelineEvent BuildEvent(
        string path, string serviceKey, DateTimeOffset at, Confidence confidence, string? caveat, int lineNumber) =>
        new()
        {
            At = at,
            Kind = EventKind.ServiceStop,
            Confidence = confidence,
            Service = serviceKey,
            Summary = "Unhandled startup exception recorded in Error.txt (log4net never started)",
            Provenance = $"{Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty)}/Error.txt:{lineNumber}",
            Caveat = caveat,
        };

    private static int LineNumberAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
