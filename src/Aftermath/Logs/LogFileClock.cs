namespace Aftermath.Logs;

using Aftermath.Contracts;

/// <summary>
/// Turns a bare time-of-day (no date, no offset — every log4net pattern in the estate uses
/// %date{HH:mm:ss.fff}) into a UTC instant, tracking two things a single line can never know
/// on its own: which calendar day it is, and which side of a DST transition its wall clock
/// falls on.
///
/// One instance is scoped to the set of physical files sharing a single FILENAME date — e.g.
/// "Prefix_2026-09-03.log" and its ".1"/".2" rolled parts, read oldest-write-time first. A
/// rolling file can hold two calendar days: if a line's time-of-day is earlier than the
/// previous line's, the day has advanced (§3.2). The carry is forward-only and, once it has
/// happened once, every subsequent event in this file is marked <c>Confidence.Inferred</c>
/// with a caveat — the derived date is no longer the one the filename asserted.
/// </summary>
public sealed class LogFileClock
{
    /// <summary>Ireland: an IANA id, resolved through ICU. Constraint 11 is why
    /// InvariantGlobalization must never be set — this lookup throws without it, on a path
    /// only reached while parsing a log file.</summary>
    public static readonly TimeZoneInfo DefaultZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    private readonly TimeZoneInfo zone;
    private DateOnly currentDate;
    private TimeOnly? lastTimeOfDay;
    private bool advancedPastMidnight;

    public LogFileClock(DateOnly filenameDate, TimeZoneInfo? zone = null)
    {
        this.currentDate = filenameDate;
        this.zone = zone ?? DefaultZone;
    }

    /// <summary>The calendar date currently in effect — the filename's date, or one day later
    /// once a midnight rollover has been detected. Exposed for Error.txt/HTTP-metric bucketing
    /// that needs "what day is it right now" without re-deriving it.</summary>
    public DateOnly CurrentDate => this.currentDate;

    public (DateTimeOffset Utc, Confidence Confidence, string? Caveat) Resolve(TimeOnly timeOfDay)
    {
        if (this.lastTimeOfDay is { } previous && timeOfDay < previous)
        {
            this.currentDate = this.currentDate.AddDays(1);
            this.advancedPastMidnight = true;
        }

        this.lastTimeOfDay = timeOfDay;

        (DateTimeOffset utc, Confidence confidence, string? caveat) =
            ToUtc(this.currentDate.ToDateTime(timeOfDay), this.zone);

        if (!this.advancedPastMidnight)
        {
            return (utc, confidence, caveat);
        }

        const string rolloverCaveat = "date advanced by midnight rollover";
        return (utc, Confidence.Inferred, caveat is null ? rolloverCaveat : $"{caveat}; {rolloverCaveat}");
    }

    /// <summary>
    /// plan.md §3.3, verbatim except that the zone is a parameter rather than a hardcoded
    /// Dublin field — the plan itself asks for --timezone to be configurable, and a
    /// compile-time-constant zone would contradict that in the same breath.
    /// </summary>
    internal static (DateTimeOffset Utc, Confidence Confidence, string? Caveat) ToUtc(DateTime localNaive, TimeZoneInfo zone)
    {
        if (zone.IsInvalidTime(localNaive))
        {
            // Spring forward: the local hour does not exist. A line stamped here means the
            // host clock was wrong or the file was hand-edited. Shift forward one hour rather
            // than guess which side of the gap was meant.
            return (new DateTimeOffset(localNaive.AddHours(1), zone.GetUtcOffset(localNaive.AddHours(1))).ToUniversalTime(),
                    Confidence.Inferred, "local time does not exist (DST gap); shifted forward one hour");
        }

        if (zone.IsAmbiguousTime(localNaive))
        {
            // Autumn back: the local hour happens twice. We cannot know which pass wrote the
            // line, so take the first (still summer time) and say so — never guess silently.
            TimeSpan[] offsets = zone.GetAmbiguousTimeOffsets(localNaive);
            TimeSpan summer = offsets.Max();
            return (new DateTimeOffset(localNaive, summer).ToUniversalTime(),
                    Confidence.Inferred, "ambiguous local time (DST overlap); assumed first pass");
        }

        return (new DateTimeOffset(localNaive, zone.GetUtcOffset(localNaive)).ToUniversalTime(),
                Confidence.Observed, null);
    }
}
