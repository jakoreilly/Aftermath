namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>
/// Fixture content is copied VERBATIM from c:\workspace\work\core-service\CHANGELOG.md,
/// including its CRLF line endings — the endings are the point of the first test.
/// </summary>
public class ChangelogParserTests
{
    // core-service/CHANGELOG.md:1-24, abridged. \r\n is deliberate and load-bearing.
    private const string CoreServiceChangelog =
        "## [1.15.0](https://bull.acme.example/acme-group/platform/core-service/compare/v1.14.0...v1.15.0) (2026-07-17)\r\n"
        + "\r\n"
        + "\r\n"
        + "### Features\r\n"
        + "\r\n"
        + "* record virtual zone Id. Closes Jira ticket [INC-7337](https://acme.atlassian.net/browse/INC-7337) ([9a6ba53](https://bull.acme.example/acme-group/platform/core-service/commit/9a6ba53cfe144195b2d45873ef59212fd810ed61))\r\n"
        + "\r\n"
        + "## [1.14.0](https://bull.acme.example/acme-group/platform/core-service/compare/v1.13.0...v1.14.0) (2026-07-17)\r\n"
        + "\r\n"
        + "\r\n"
        + "### Bug Fixes\r\n"
        + "\r\n"
        + "* Convert user reference to app token ([d7a13eb](https://bull.acme.example/acme-group/platform/core-service/commit/d7a13ebab2d76a5050055edd6fda32502b857787))\r\n";

    [Fact]
    public void Parses_crlf_headings()
    {
        // Regression guard: .NET's Multiline '$' does not match before '\r', so a pattern
        // ending "[ \t]*$" finds ZERO entries in every CHANGELOG.md in the estate while the
        // same expression works in JavaScript. Found by running Phase 2 against the real
        // workspace: the releases appeared, but with no tickets and no changelog anchor.
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse(CoreServiceChangelog);

        Assert.Equal(2, entries.Count);
        Assert.Equal("1.15.0", entries[0].Version);
        Assert.Equal("1.14.0", entries[1].Version);
    }

    [Fact]
    public void Records_the_heading_line_for_provenance()
    {
        IReadOnlyList<ChangelogEntry> entries = ChangelogParser.Parse(CoreServiceChangelog);

        Assert.Equal(1, entries[0].Line);
        Assert.Equal(8, entries[1].Line);
    }

    [Fact]
    public void Dates_are_midnight_utc_because_the_heading_carries_no_time()
    {
        ChangelogEntry entry = ChangelogParser.Parse(CoreServiceChangelog)[0];

        Assert.Equal(new DateTimeOffset(2026, 7, 17, 0, 0, 0, TimeSpan.Zero), entry.Date);
        Assert.Equal(TimeSpan.Zero, entry.Date.Offset);
    }

    [Fact]
    public void Keeps_only_the_first_of_a_duplicated_version()
    {
        // api-gateway2 lists every version twice: 40 headings, 34 tags, no untagged version.
        string doubled = CoreServiceChangelog + CoreServiceChangelog;

        Assert.Equal(2, ChangelogParser.Parse(doubled).Count);
    }

    [Fact]
    public void Parses_the_unlinked_heading_form()
    {
        IReadOnlyList<ChangelogEntry> entries =
            ChangelogParser.Parse("# 1.0.0 (2025-04-02)\r\n\r\n* first cut\r\n");

        Assert.Equal("1.0.0", Assert.Single(entries).Version);
    }

    [Fact]
    public void Headline_drops_the_link_urls_but_keeps_the_ticket_key()
    {
        string headline = ChangelogParser.Headline(ChangelogParser.Parse(CoreServiceChangelog)[0].Body);

        Assert.Equal("record virtual zone Id. Closes Jira ticket INC-7337", headline);
        Assert.DoesNotContain("http", headline, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("v1.15.0", "1.15.0")]
    [InlineData("1.15.0", "1.15.0")]
    [InlineData("V2.0.0", "2.0.0")]
    [InlineData("version-tag", "version-tag")]
    public void Normalises_the_leading_v_of_a_tag(string input, string expected) =>
        Assert.Equal(expected, ChangelogParser.NormaliseVersion(input));

    [Fact]
    public void Empty_input_yields_no_entries() => Assert.Empty(ChangelogParser.Parse(string.Empty));
}
