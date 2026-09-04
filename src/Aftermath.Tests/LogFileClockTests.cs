namespace Aftermath.Tests;

using Aftermath.Contracts;
using Aftermath.Logs;

public sealed class LogFileClockTests
{
    [Fact]
    public void Ordinary_line_is_observed_with_no_caveat()
    {
        var clock = new LogFileClock(new DateOnly(2026, 9, 3));

        (DateTimeOffset utc, Confidence confidence, string? caveat) = clock.Resolve(new TimeOnly(14, 0, 0));

        // 14:00 IST (summer time, +01:00) on 2026-09-03.
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 13, 0, 0, TimeSpan.Zero), utc);
        Assert.Equal(Confidence.Observed, confidence);
        Assert.Null(caveat);
    }

    [Fact]
    public void Midnight_rollover_advances_the_date_and_caveats_the_event()
    {
        var clock = new LogFileClock(new DateOnly(2026, 9, 3));
        clock.Resolve(new TimeOnly(23, 59, 0));

        (DateTimeOffset utc, Confidence confidence, string? caveat) = clock.Resolve(new TimeOnly(0, 5, 0));

        Assert.Equal(new DateOnly(2026, 9, 4), clock.CurrentDate);
        Assert.Equal(new DateTimeOffset(2026, 9, 3, 23, 5, 0, TimeSpan.Zero), utc); // 00:05 IST 9/4 == 23:05 UTC 9/3
        Assert.Equal(Confidence.Inferred, confidence);
        Assert.Equal("date advanced by midnight rollover", caveat);
    }

    [Fact]
    public void Every_event_after_the_first_carry_stays_caveated()
    {
        var clock = new LogFileClock(new DateOnly(2026, 9, 3));
        clock.Resolve(new TimeOnly(23, 59, 0));
        clock.Resolve(new TimeOnly(0, 5, 0)); // triggers the carry

        (_, Confidence confidence, string? caveat) = clock.Resolve(new TimeOnly(0, 10, 0));

        Assert.Equal(Confidence.Inferred, confidence);
        Assert.Equal("date advanced by midnight rollover", caveat);
    }

    [Fact]
    public void Ambiguous_DST_line_takes_the_first_summer_time_pass()
    {
        // Ireland's clocks go back on the last Sunday of October — 2026-10-25 01:30 occurs
        // twice: once at IST (+01:00) and once at GMT (+00:00).
        var clock = new LogFileClock(new DateOnly(2026, 10, 25));

        (DateTimeOffset utc, Confidence confidence, string? caveat) = clock.Resolve(new TimeOnly(1, 30, 0));

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), utc); // 01:30 +01:00
        Assert.Equal(Confidence.Inferred, confidence);
        Assert.Equal("ambiguous local time (DST overlap); assumed first pass", caveat);
    }

    [Fact]
    public void Invalid_DST_line_is_shifted_forward_one_hour()
    {
        // Ireland's clocks go forward on the last Sunday of March — 01:00-02:00 on
        // 2026-03-29 does not exist.
        var clock = new LogFileClock(new DateOnly(2026, 3, 29));

        (DateTimeOffset utc, Confidence confidence, string? caveat) = clock.Resolve(new TimeOnly(1, 30, 0));

        Assert.Equal(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero), utc); // shifted to 02:30 +01:00
        Assert.Equal(Confidence.Inferred, confidence);
        Assert.Equal("local time does not exist (DST gap); shifted forward one hour", caveat);
    }

    [Fact]
    public void Default_zone_is_europe_dublin()
    {
        Assert.Equal("Europe/Dublin", LogFileClock.DefaultZone.Id);
    }
}
