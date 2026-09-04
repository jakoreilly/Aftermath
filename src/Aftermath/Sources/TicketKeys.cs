namespace Aftermath.Sources;

using System.Text.RegularExpressions;

/// <summary>
/// Pulls JIRA keys out of commit subjects and changelog bodies.
/// TRAN 2251 uses, PMOB 172, PMD 4 across the estate's changelogs (Phase 0 count).
/// </summary>
public static partial class TicketKeys
{
    /// <summary>The estate's three JIRA projects, in Phase 0 frequency order.</summary>
    public static readonly IReadOnlyList<string> DefaultPrefixes = ["TRAN", "PMOB", "PMD"];

    // An unanchored [A-Z]{2,10}-\d+ also matches UTF-8, SHA-256 and NET-6, so the prefix is
    // captured and then checked against an allow-list in Extract. The allow-list cannot live
    // inside the pattern: [GeneratedRegex] takes a compile-time literal and --ticket-prefixes
    // is a runtime flag. Filtering in code keeps both, and keeps runtime Regex construction out
    // (hard constraint 8, S6444).
    [GeneratedRegex(
        @"\b(?<prefix>[A-Z]{2,10})-(?<number>[0-9]{1,6})\b",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex CandidateKey();

    /// <summary>
    /// Distinct keys in first-seen order — order is part of the golden-test surface in Phase 4,
    /// so it must not depend on hashing.
    /// </summary>
    public static IReadOnlyList<string> Extract(string? text, IReadOnlyCollection<string> prefixes)
    {
        if (string.IsNullOrEmpty(text) || prefixes.Count == 0)
        {
            return [];
        }

        List<string>? found = null;
        foreach (Match m in CandidateKey().Matches(text))
        {
            string prefix = m.Groups["prefix"].Value;
            if (!prefixes.Contains(prefix, StringComparer.Ordinal))
            {
                continue;
            }

            string key = $"{prefix}-{m.Groups["number"].Value}";
            found ??= [];
            if (!found.Contains(key, StringComparer.Ordinal))
            {
                found.Add(key);
            }
        }

        return (IReadOnlyList<string>?)found ?? [];
    }

    /// <summary>Parses the --ticket-prefixes flag. Null or blank yields the default three.</summary>
    public static IReadOnlyList<string> ParsePrefixes(string? flagValue)
    {
        if (string.IsNullOrWhiteSpace(flagValue))
        {
            return DefaultPrefixes;
        }

        string[] parsed = flagValue
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return parsed.Length == 0 ? DefaultPrefixes : parsed;
    }
}
