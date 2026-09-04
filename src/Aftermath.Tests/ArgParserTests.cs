namespace Aftermath.Tests;

using Aftermath.Cli;

public class ArgParserTests
{
    [Fact]
    public void Parse_ReadsFlagValuePairs()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["--workspace", @"c:\workspace\work"]);
        Assert.Equal(@"c:\workspace\work", flags.Get("workspace"));
    }

    [Fact]
    public void Parse_TreatsABareFlagAsTrue()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["--online", "--workspace", "w"]);
        Assert.True(flags.GetBool("online"));
        Assert.Equal("w", flags.Get("workspace"));
    }

    [Fact]
    public void Parse_IsCaseInsensitiveOnKeys()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["--Workspace", "w"]);
        Assert.Equal("w", flags.Get("workspace"));
    }

    [Fact]
    public void GetBool_HonoursExplicitFalse()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["--online", "false"]);
        Assert.False(flags.GetBool("online"));
    }

    [Fact]
    public void GetInt_FallsBackWhenAbsentOrUnparseable()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["--limit", "abc"]);
        Assert.Equal(500, flags.GetInt("limit", 500));
        Assert.Equal(500, flags.GetInt("missing", 500));
    }

    [Fact]
    public void Parse_IgnoresPositionalTokens()
    {
        Dictionary<string, string?> flags = ArgParser.Parse(["stray", "--workspace", "w"]);
        Assert.Single(flags);
        Assert.Equal("w", flags.Get("workspace"));
    }
}
