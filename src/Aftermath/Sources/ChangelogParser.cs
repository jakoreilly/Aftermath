namespace Aftermath.Sources;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// One "## [1.15.0](compare-url) (2026-07-17)" block from a semantic-release CHANGELOG.md.
/// <see cref="Date"/> is midnight UTC of a DATE-ONLY heading — the file records no time of
/// day at all, which is why a changelog-only release is <c>Reported</c> and carries a caveat.
/// </summary>
public sealed record ChangelogEntry
{
    public required string Version { get; init; }

    /// <summary>1-based line of the heading, for the "key/CHANGELOG.md:12" provenance.</summary>
    public required int Line { get; init; }

    public required DateTimeOffset Date { get; init; }

    /// <summary>The bullet lines under the heading, joined — the source of the ticket keys.</summary>
    public required string Body { get; init; }
}

/// <summary>Pure text in, entries out — no disk access, so it is directly unit testable.</summary>
public static partial class ChangelogParser
{
    // Both forms semantic-release emits:
    //   ## [1.15.0](https://.../compare/v1.14.0...v1.15.0) (2026-07-17)
    //   ## 1.15.0 (2026-07-17)
    // The "# 1.0.0 (2025-…)" first-release heading uses one hash, so the leading run is loose.
    // The \r in the trailing class is load-bearing: every CHANGELOG.md in the estate is CRLF,
    // and .NET's Multiline '$' matches only before '\n' — unlike JavaScript's, which also
    // matches before '\r'. Omit it and this pattern silently matches nothing at all.
    [GeneratedRegex(
        @"^\#{1,3}[ \t]+\[?(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.\-]+)?)\]?[^\r\n]*?\((?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})\)[ \t\r]*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionHeading();

    /// <summary>
    /// Entries in file order, first occurrence winning. api-gateway2 lists every version
    /// TWICE in its CHANGELOG.md (40 headings, 34 tags, zero untagged versions — measured
    /// 2026-09-04), so de-duplication is required, not defensive.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Parse(string changelogMarkdown)
    {
        if (string.IsNullOrWhiteSpace(changelogMarkdown))
        {
            return [];
        }

        MatchCollection headings = VersionHeading().Matches(changelogMarkdown);
        if (headings.Count == 0)
        {
            return [];
        }

        var entries = new List<ChangelogEntry>(headings.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < headings.Count; i++)
        {
            Match m = headings[i];
            string version = m.Groups["version"].Value;
            if (!seen.Add(version) || !TryParseDate(m.Groups["date"].Value, out DateTimeOffset date))
            {
                continue;
            }

            int bodyStart = m.Index + m.Length;
            int bodyEnd = i + 1 < headings.Count ? headings[i + 1].Index : changelogMarkdown.Length;
            entries.Add(new ChangelogEntry
            {
                Version = version,
                Line = LineNumberAt(changelogMarkdown, m.Index),
                Date = date,
                Body = changelogMarkdown[bodyStart..bodyEnd].Trim(),
            });
        }

        return entries;
    }

    /// <summary>Strips the "v" a tag carries so "v1.15.0" joins "1.15.0".</summary>
    public static string NormaliseVersion(string tagOrVersion) =>
        tagOrVersion.Length > 1 && (tagOrVersion[0] is 'v' or 'V') && char.IsAsciiDigit(tagOrVersion[1])
            ? tagOrVersion[1..]
            : tagOrVersion;

    /// <summary>
    /// First bullet lines of the body, flattened to one line for a timeline summary. Every
    /// bullet semantic-release writes ends in a "([9a6ba53](commit-url))" back-reference and
    /// often links its JIRA key too; left in, one release's detail runs past 400 characters of
    /// bull.acme.example URLs. The link TEXT is kept — that is where the ticket key lives.
    /// </summary>
    public static string Headline(string body, int maxBullets = 3)
    {
        string[] bullets = body
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("* ", StringComparison.Ordinal))
            .Select(l => FlattenLinks(l[2..]).Trim())
            .Where(l => l.Length > 0)
            .Take(maxBullets)
            .ToArray();

        return string.Join("; ", bullets);
    }

    /// <summary>"[INC-7337](url) ([9a6ba53](url))" becomes "INC-7337".</summary>
    private static string FlattenLinks(string text) =>
        TrailingCommitRef().Replace(MarkdownLink().Replace(text, "${text}"), string.Empty).Trim();

    [GeneratedRegex(
        @"\[(?<text>[^\]]*)\]\((?<url>[^)\s]*)\)",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex MarkdownLink();

    // What is left of the back-reference once the links are flattened: " (9a6ba53)".
    [GeneratedRegex(
        @"\s*\([0-9a-f]{7,40}\)\s*$",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingCommitRef();

    private static bool TryParseDate(string yyyyMmDd, out DateTimeOffset date)
    {
        if (DateOnly.TryParseExact(yyyyMmDd, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly d))
        {
            date = new DateTimeOffset(d.Year, d.Month, d.Day, 0, 0, 0, TimeSpan.Zero);
            return true;
        }

        date = default;
        return false;
    }

    private static int LineNumberAt(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
