namespace Aftermath.Sources;

using System.Text.RegularExpressions;

/// <summary>Extracts a GitLab project path ("group/subgroup/project") from a clone's own
/// remote URL — both forms this estate's origins use: <c>git@host:path.git</c> and
/// <c>https://host/path.git</c>. Reusing the clone's actual remote, via the existing
/// <see cref="IGitRunner"/> seam, is always accurate; nothing here guesses at a URL shape.</summary>
public static partial class GitLabRemote
{
    [GeneratedRegex(@"^(?:git@[^:]+:|https?://[^/]+/)(?<path>.+?)(?:\.git)?/?$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RemotePattern();

    public static string? TryExtractPath(string remoteUrl)
    {
        Match m = RemotePattern().Match(remoteUrl.Trim());
        return m.Success ? m.Groups["path"].Value : null;
    }
}
