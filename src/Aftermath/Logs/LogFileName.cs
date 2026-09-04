namespace Aftermath.Logs;

using System.Text.RegularExpressions;

/// <summary>
/// Extracts the calendar date encoded in a rolled log4net filename. Two datePattern variants
/// exist estate-wide (Phase 0 measurement): "yyyy-MM-dd.log" (41 configs) and "yyyyMMdd.log"
/// (9 configs). A size-rolled part carries a numeric suffix the appender appends, e.g.
/// "Acme.Ledger.CoreService.WebApi_2026-09-03.log.1" — §3.2's Composite rolling style.
/// </summary>
public static partial class LogFileName
{
    [GeneratedRegex(
        @"_(?:(?<y1>\d{4})-(?<mo1>\d{2})-(?<d1>\d{2})|(?<y2>\d{4})(?<mo2>\d{2})(?<d2>\d{2}))\.log(?:\.\d+)?$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DateSuffix();

    public static DateOnly? TryReadDate(string fileName)
    {
        Match m = DateSuffix().Match(fileName);
        if (!m.Success)
        {
            return null;
        }

        (string y, string mo, string d) = m.Groups["y1"].Success
            ? (m.Groups["y1"].Value, m.Groups["mo1"].Value, m.Groups["d1"].Value)
            : (m.Groups["y2"].Value, m.Groups["mo2"].Value, m.Groups["d2"].Value);

        return int.TryParse(y, out int year) && int.TryParse(mo, out int month) && int.TryParse(d, out int day)
            ? new DateOnly(year, month, day)
            : null;
    }
}
