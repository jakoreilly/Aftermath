namespace Aftermath.Correlation;

using Aftermath.Contracts;

/// <summary>
/// Scores every change event (Release, Deploy, Commit) for proximity to the incident, so "what
/// changed?" is answered mechanically rather than from memory (Goal C). Pure, no I/O.
///
/// The weights are measured, not guessed (§4.2): median inter-release gap across seven repos is
/// 363–988 hours, so a release inside a 24-hour look-back is genuinely rare and deserves a high
/// score. Do not change IncidentWindow.Default or these weights without re-running that
/// measurement — they are only meaningful relative to the cadence they were derived from.
/// </summary>
public static class SuspectRanker
{
    public static IReadOnlyList<Suspect> Rank(Timeline timeline)
    {
        var errorServices = new HashSet<string>(
            timeline.Events.Where(e => e.Kind is EventKind.LogError or EventKind.LogFatal).Select(e => e.Service),
            StringComparer.Ordinal);

        List<Suspect> suspects =
        [
            .. timeline.Events
                .Where(e => e.Kind is EventKind.Release or EventKind.Deploy or EventKind.Commit)
                .Select(e => new Suspect
                {
                    Event = e,
                    Score = Proximity(e.At, timeline.Window.AtUtc) * KindWeight(e.Kind) * BlastRadius(e.Service, errorServices),
                }),
        ];

        return
        [
            .. suspects
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Event.At)
                .ThenBy(s => s.Event.Service, StringComparer.Ordinal),
        ];
    }

    /// <summary>1.0 within 2h before the incident; 0.6 within 6h; 0.3 within 24h; 0.1
    /// otherwise — including anything at or after the incident, which cannot have been
    /// affected by something that had not happened yet.</summary>
    private static double Proximity(DateTimeOffset at, DateTimeOffset incidentAt)
    {
        TimeSpan before = incidentAt - at;
        if (before <= TimeSpan.Zero || before > TimeSpan.FromHours(24))
        {
            return 0.1;
        }

        if (before <= TimeSpan.FromHours(2))
        {
            return 1.0;
        }

        return before <= TimeSpan.FromHours(6) ? 0.6 : 0.3;
    }

    private static double KindWeight(EventKind kind) => kind switch
    {
        EventKind.Deploy => 1.0,
        EventKind.Release => 0.8,
        EventKind.Commit => 0.3,
        _ => 0.0,
    };

    /// <summary>1.0 plus 0.25 per OTHER service showing an error in the window — a change to a
    /// shared dependency that broke several downstream services scores higher than the same
    /// change in isolation.</summary>
    private static double BlastRadius(string service, HashSet<string> errorServices)
    {
        int otherServicesWithErrors = errorServices.Count(s => !string.Equals(s, service, StringComparison.Ordinal));
        return 1.0 + (0.25 * otherServicesWithErrors);
    }
}
