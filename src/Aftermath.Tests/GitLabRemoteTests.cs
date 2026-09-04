namespace Aftermath.Tests;

using Aftermath.Sources;

public sealed class GitLabRemoteTests
{
    [Theory]
    [InlineData("git@bull.acme.example:acme-group/platform/services/core-service.git", "acme-group/platform/services/core-service")]
    [InlineData("https://bull.acme.example/acme-group/platform/services/core-service.git", "acme-group/platform/services/core-service")]
    [InlineData("https://bull.acme.example/acme-group/platform/services/core-service", "acme-group/platform/services/core-service")]
    public void Extracts_the_project_path_from_both_remote_url_forms(string remoteUrl, string expected)
    {
        Assert.Equal(expected, GitLabRemote.TryExtractPath(remoteUrl));
    }

    [Fact]
    public void Returns_null_for_an_unrecognisable_remote()
    {
        Assert.Null(GitLabRemote.TryExtractPath("not a url at all"));
    }
}
