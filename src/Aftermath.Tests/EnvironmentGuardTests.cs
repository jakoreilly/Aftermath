namespace Aftermath.Tests;

/// <summary>
/// Guards environment assumptions the rest of the tool is built on. These are not testing
/// our code — they are testing that the ground it stands on has not moved. They live here
/// so a machine or SDK that cannot support the design fails loudly at test time rather than
/// halfway through parsing a log file.
/// </summary>
public class EnvironmentGuardTests
{
    [Fact]
    public void EuropeDublin_ResolvesByItsIanaId()
    {
        // Hard constraint 11: .NET accepts IANA ids on Windows only through ICU. If
        // InvariantGlobalization is ever switched on, this throws — and it would otherwise
        // only throw on the log-parsing path, at the worst possible moment.
        TimeZoneInfo dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

        Assert.NotNull(dublin);
    }

    [Fact]
    public void EuropeDublin_StillObservesDaylightSaving()
    {
        // The whole DST design in Phase 3 is pointless if the zone has no transitions.
        TimeZoneInfo dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

        Assert.True(dublin.SupportsDaylightSavingTime);
    }

    [Fact]
    public void TheAutumnOverlapHourIsAmbiguous_AndTheSpringGapHourIsInvalid()
    {
        // These two facts are exactly what makes a log line's HH:mm:ss.fff undatable for one
        // hour a year in each direction. 2026 transitions: 29 March and 25 October.
        TimeZoneInfo dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

        Assert.True(dublin.IsAmbiguousTime(new DateTime(2026, 10, 25, 1, 30, 0, DateTimeKind.Unspecified)));
        Assert.True(dublin.IsInvalidTime(new DateTime(2026, 3, 29, 1, 30, 0, DateTimeKind.Unspecified)));
    }

    [Fact]
    public void CommitTimestampsWithDifferingOffsets_AllNormaliseToUtc()
    {
        // accountservice's last 200 commits carry three different offsets (Z, +01:00,
        // +02:00). DateTimeOffset handles each; DateTime.Parse would not.
        DateTimeOffset z = DateTimeOffset.Parse("2026-07-13T15:23:51Z", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset ist = DateTimeOffset.Parse("2026-07-17T23:34:17+01:00", System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset cest = DateTimeOffset.Parse("2026-07-17T23:34:17+02:00", System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(TimeSpan.Zero, z.ToUniversalTime().Offset);
        Assert.Equal(22, ist.ToUniversalTime().Hour);
        Assert.Equal(21, cest.ToUniversalTime().Hour);
    }
}
