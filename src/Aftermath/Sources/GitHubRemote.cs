namespace Aftermath.Sources;

using System.Text.RegularExpressions;

/// <summary>Extracts an <c>owner/repo</c> slug from a clone's own remote URL — both forms a
/// GitHub origin takes: <c>git@github.com:owner/repo.git</c> and
/// <c>https://github.com/owner/repo.git</c>. Mirrors <see cref="GitLabRemote"/>; reusing the
/// clone's actual remote, via the existing <see cref="IGitRunner"/> seam, is always accurate
/// — nothing here guesses at a URL shape. GitHub repo paths are always exactly two segments,
/// so anything deeper (or shallower) is rejected rather than truncated.</summary>
public static partial class GitHubRemote
{
    [GeneratedRegex(@"^(?:git@[^:]+:|https?://[^/]+/)(?<owner>[^/]+)/(?<repo>[^/]+?)(?:\.git)?/?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RemotePattern();

    public static string? TryExtractSlug(string remoteUrl)
    {
        Match m = RemotePattern().Match(remoteUrl.Trim());
        return m.Success ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
    }
}
