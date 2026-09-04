namespace Aftermath.Tests;

using Aftermath.Sources;

public class TicketKeysTests
{
    private static IReadOnlyList<string> Extract(string text) =>
        TicketKeys.Extract(text, TicketKeys.DefaultPrefixes);

    [Fact]
    public void Finds_the_estates_three_project_prefixes()
    {
        Assert.Equal(new[] { "INC-7337" }, Extract("feat: record virtual zone Id. Closes INC-7337"));
        Assert.Equal(new[] { "PMOB-172" }, Extract("fix: PMOB-172 retry the token refresh"));
        Assert.Equal(new[] { "PMD-4" }, Extract("chore: PMD-4"));
    }

    [Fact]
    public void Rejects_the_look_alikes_an_unanchored_pattern_would_swallow()
    {
        // The exact reason the prefix set is an allow-list rather than [A-Z]{2,10}-\d+.
        Assert.Empty(Extract("switch the encoding to UTF-8 and the hash to SHA-256 on NET-6"));
    }

    [Fact]
    public void Keeps_distinct_keys_in_first_seen_order()
    {
        // Order is part of Phase 4's golden-test surface, so it must not depend on hashing.
        Assert.Equal(
            new[] { "PMOB-172", "INC-7337" },
            Extract("PMOB-172 then INC-7337 then PMOB-172 again"));
    }

    [Fact]
    public void Pulls_the_key_out_of_a_markdown_link()
    {
        Assert.Equal(
            new[] { "INC-7337" },
            Extract("Closes [INC-7337](https://acme.atlassian.net/browse/INC-7337)"));
    }

    [Fact]
    public void Honours_a_custom_prefix_list()
    {
        IReadOnlyList<string> prefixes = TicketKeys.ParsePrefixes("ops, sec");

        Assert.Equal(new[] { "OPS", "SEC" }, prefixes);
        Assert.Equal(new[] { "OPS-9" }, TicketKeys.Extract("OPS-9 and INC-1 raised", prefixes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",,,")]
    public void Blank_prefix_flag_falls_back_to_the_default_three(string? flagValue) =>
        Assert.Equal(TicketKeys.DefaultPrefixes, TicketKeys.ParsePrefixes(flagValue));

    [Fact]
    public void Null_or_empty_text_yields_no_keys()
    {
        Assert.Empty(Extract(string.Empty));
        Assert.Empty(TicketKeys.Extract(null, TicketKeys.DefaultPrefixes));
    }
}
