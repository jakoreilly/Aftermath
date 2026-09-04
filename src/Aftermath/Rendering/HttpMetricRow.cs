namespace Aftermath.Rendering;

using System.Globalization;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>Recovers the structured numbers <see cref="Logs.HttpMetricsAccumulator"/> packed
/// into <see cref="TimelineEvent.Detail"/> ("requests=N 4xx=N 5xx=N p50Ms=N p95Ms=N") for the
/// "Error rate and latency" table. Parsing the fixed format back rather than widening
/// TimelineEvent with HTTP-specific fields every other event kind would carry as null.</summary>
public readonly partial record struct HttpMetricRow(int Requests, int FourXx, int FiveXx, int P95Ms)
{
    [GeneratedRegex(
        @"requests=(?<requests>\d+) 4xx=(?<fourxx>\d+) 5xx=(?<fivexx>\d+) p50Ms=\d+ p95Ms=(?<p95>\d+)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Pattern();

    public static bool TryParse(TimelineEvent e, out HttpMetricRow row)
    {
        if (e.Detail is { } detail && Pattern().Match(detail) is { Success: true } m)
        {
            row = new HttpMetricRow(
                int.Parse(m.Groups["requests"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["fourxx"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["fivexx"].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups["p95"].Value, CultureInfo.InvariantCulture));
            return true;
        }

        row = default;
        return false;
    }
}
