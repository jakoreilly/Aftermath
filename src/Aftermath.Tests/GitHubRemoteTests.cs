namespace Aftermath.Tests;

using Aftermath.Sources;

public sealed class GitHubRemoteTests
{
    [Theory]
    [InlineData("git@github.com:jakoreilly/Aftermath.git", "jakoreilly/Aftermath")]
    [InlineData("https://github.com/jakoreilly/Aftermath.git", "jakoreilly/Aftermath")]
    [InlineData("https://github.com/jakoreilly/Aftermath", "jakoreilly/Aftermath")]
    [InlineData("git@github.enterprise.example:acme-org/core-service.git", "acme-org/core-service")]
    public void Extracts_the_owner_repo_slug_from_every_remote_url_form(string remoteUrl, string expected)
    {
        Assert.Equal(expected, GitHubRemote.TryExtractSlug(remoteUrl));
    }

    [Fact]
    public void Returns_null_for_an_unrecognisable_remote()
    {
        Assert.Null(GitHubRemote.TryExtractSlug("not a url at all"));
    }

    [Fact]
    public void Returns_null_for_a_deeper_than_owner_repo_path()
    {
        Assert.Null(GitHubRemote.TryExtractSlug("https://gitlab.example/group/subgroup/project.git"));
    }
}
