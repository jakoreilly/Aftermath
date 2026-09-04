namespace Aftermath.Tests;

using Aftermath.Cli;

public class TimeArgsTests
{
    [Fact]
    public void Parses_a_utc_instant()
    {
        DateTimeOffset at = TimeArgs.ParseInstant("--at", "2026-07-17T13:00:00Z");

        Assert.Equal(new DateTimeOffset(2026, 7, 17, 13, 0, 0, TimeSpan.Zero), at);
        Assert.Equal(TimeSpan.Zero, at.Offset);
    }

    [Fact]
    public void Converts_an_offset_instant_to_utc()
    {
        // The operator quotes Dublin wall-clock; the tool works in UTC from the first parse.
        DateTimeOffset at = TimeArgs.ParseInstant("--at", "2026-07-17T13:19:21+01:00");

        Assert.Equal(new DateTimeOffset(2026, 7, 17, 12, 19, 21, TimeSpan.Zero), at);
    }

    [Fact]
    public void Rejects_an_instant_with_no_zone_rather_than_guessing_one()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => TimeArgs.ParseInstant("--at", "2026-07-17T13:00:00"));

        Assert.Contains("no time zone", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Requires_the_instant(string? value) =>
        Assert.Throws<ArgumentException>(() => TimeArgs.ParseInstant("--at", value));

    [Fact]
    public void Rejects_a_non_iso_instant() =>
        Assert.Throws<ArgumentException>(() => TimeArgs.ParseInstant("--at", "yesterday teatime"));

    [Theory]
    [InlineData("24h", 24 * 60)]
    [InlineData("2h", 120)]
    [InlineData("90m", 90)]
    [InlineData("2d", 2 * 24 * 60)]
    [InlineData("120s", 2)]
    [InlineData("36", 36 * 60)]
    public void Parses_durations(string value, double expectedMinutes) =>
        Assert.Equal(
            TimeSpan.FromMinutes(expectedMinutes),
            TimeArgs.ParseDuration("--window", value, TimeSpan.Zero));

    [Fact]
    public void Missing_duration_takes_the_fallback() =>
        Assert.Equal(
            TimeSpan.FromHours(2),
            TimeArgs.ParseDuration("--forward", null, TimeSpan.FromHours(2)));

    [Theory]
    [InlineData("0h")]
    [InlineData("-4h")]
    [InlineData("soon")]
    [InlineData("24y")]
    public void Rejects_a_non_positive_or_unknown_duration(string value) =>
        Assert.Throws<ArgumentException>(() => TimeArgs.ParseDuration("--window", value, TimeSpan.Zero));
}
