namespace Aftermath.Cli;

using System.Globalization;

/// <summary>
/// Parses the two time-shaped flags. Pure, so the awkward cases are unit tests rather than
/// CLI runs. Both are strict on purpose: constraint 5 says a local time must never cross a
/// function boundary, and the cheapest place to enforce that is where the operator types it.
/// </summary>
public static class TimeArgs
{
    /// <summary>
    /// "--at 2026-07-17T13:00:00Z". An offset is REQUIRED. A bare "2026-07-17T13:00:00" is
    /// rejected rather than assumed UTC: the whole tool exists because an hour's silent drift
    /// makes a timeline confidently wrong, and guessing here would be the first such guess.
    /// </summary>
    public static DateTimeOffset ParseInstant(string flagName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                $"{flagName} is required, e.g. {flagName} 2026-07-17T13:00:00Z");
        }

        string text = value.Trim();
        if (!DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            throw new ArgumentException(
                $"{flagName} '{text}' is not an ISO-8601 instant. Expected e.g. 2026-07-17T13:00:00Z.");
        }

        if (!HasExplicitOffset(text))
        {
            throw new ArgumentException(
                $"{flagName} '{text}' has no time zone. Append 'Z' for UTC or an offset such as '+01:00' — "
                + "this tool never guesses a zone.");
        }

        return parsed.ToUniversalTime();
    }

    /// <summary>"24h", "90m", "2d", "45s", or a bare number of hours. Must be positive.</summary>
    public static TimeSpan ParseDuration(string flagName, string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string text = value.Trim();
        char unit = char.ToLowerInvariant(text[^1]);
        string number = char.IsAsciiDigit(unit) ? text : text[..^1];

        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double magnitude) ||
            magnitude <= 0 || double.IsInfinity(magnitude))
        {
            throw new ArgumentException(
                $"{flagName} '{text}' is not a positive duration. Expected e.g. 24h, 90m, 2d or 45s.");
        }

        return unit switch
        {
            's' => TimeSpan.FromSeconds(magnitude),
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            'd' => TimeSpan.FromDays(magnitude),
            _ when char.IsAsciiDigit(unit) => TimeSpan.FromHours(magnitude),
            _ => throw new ArgumentException(
                $"{flagName} '{text}' has unknown unit '{unit}'. Use s, m, h or d."),
        };
    }

    private static bool HasExplicitOffset(string text)
    {
        if (text.EndsWith('Z') || text.EndsWith('z'))
        {
            return true;
        }

        // "+01:00" / "-0500" at the tail, without mistaking the date's own hyphens for a sign.
        int timeStart = text.IndexOf('T');
        return timeStart >= 0 && text.LastIndexOfAny(['+', '-']) > timeStart;
    }
}
