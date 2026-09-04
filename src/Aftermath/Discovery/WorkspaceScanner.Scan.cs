namespace Aftermath.Discovery;

using Aftermath.Contracts;

/// <summary>The disk-touching half of the scanner. Everything here is a thin shell over the
/// pure extractors in WorkspaceScanner.cs, which carry the unit tests.</summary>
public static partial class WorkspaceScanner
{
    private static readonly string[] ExcludedDirSegments =
        [$"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
         $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"];

    /// <summary>
    /// Builds one manifest per immediate subdirectory of <paramref name="workspacePath"/>
    /// that contains a .gitlab-ci.yml. Ordered by key so output is stable between runs.
    /// Never writes, and never throws for a single unreadable repo — a workspace with one
    /// bad clone should still yield the other twenty-five.
    /// </summary>
    public static IReadOnlyList<ServiceManifest> Scan(string workspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);

        if (!Directory.Exists(workspacePath))
        {
            throw new DirectoryNotFoundException(
                $"Workspace not found: {workspacePath}. Pass --workspace pointing at the directory that holds your clones.");
        }

        List<ServiceManifest> manifests = [];

        foreach (string repoDir in EnumerateRepoDirectories(workspacePath))
        {
            ServiceManifest? manifest = TryBuildOne(repoDir, Path.Combine(repoDir, ".gitlab-ci.yml"));
            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Repo roots are normally immediate subdirectories, but three of the shared-library
    /// repos in the real workspace were unzipped from archives and sit one level deeper
    /// (Acme.shared-master/Acme.shared-master/.gitlab-ci.yml). Their commits are real
    /// evidence — a shared-library bump is a classic incident cause — so look one level down
    /// when the immediate directory has no .gitlab-ci.yml of its own. Depth stops at two:
    /// deeper recursion would start matching sample and template projects.
    /// </summary>
    private static IEnumerable<string> EnumerateRepoDirectories(string workspacePath)
    {
        foreach (string dir in Directory.EnumerateDirectories(workspacePath))
        {
            if (File.Exists(Path.Combine(dir, ".gitlab-ci.yml")))
            {
                yield return dir;
                continue;
            }

            IEnumerable<string> nested;
            try
            {
                nested = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string child in nested)
            {
                if (File.Exists(Path.Combine(child, ".gitlab-ci.yml")))
                {
                    yield return child;
                }
            }
        }
    }

    private static ServiceManifest? TryBuildOne(string repoDir, string ciPath)
    {
        try
        {
            string ci = ReadShared(ciPath);
            (string? logPrefix, bool prefixIsToken, IReadOnlyList<string> patterns) = ReadLog4NetFacts(repoDir);

            return new ServiceManifest
            {
                Key = Path.GetFileName(repoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                RepoPath = repoDir,
                OctopusProjectSlug = ReadOctopusSlug(ci),
                PackageName = ReadPackageName(ci),
                OtelServiceName = ReadOtelName(repoDir),
                LogFilePrefix = logPrefix,
                LogPrefixIsToken = prefixIsToken,
                LogPatterns = patterns,
            };
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Merges every log4net*.config in the repo. The base config carries a usable relative
    /// prefix ("logs\X_"); the Release config carries the Octopus token
    /// "#{Acme.Logs.Path.And.File.Prefix}". Prefer the usable one, but report that the
    /// production path is a token when that is all we have.
    /// </summary>
    private static (string? Prefix, bool IsToken, IReadOnlyList<string> Patterns) ReadLog4NetFacts(string repoDir)
    {
        string? usablePrefix = null;
        string? tokenPrefix = null;
        List<string> patterns = [];

        foreach (string path in EnumerateFiles(repoDir, "log4net*.config"))
        {
            string xml = ReadShared(path);

            string? prefix = ReadLogFilePrefix(xml);
            if (prefix is not null)
            {
                if (IsOctopusToken(prefix))
                {
                    tokenPrefix ??= prefix;
                }
                else
                {
                    usablePrefix ??= prefix;
                }
            }

            patterns.AddRange(ReadLogPatterns(xml));
        }

        // Report the USABLE prefix when there is one — it is what matches filenames under
        // --log-root. But the flag answers a different, more important question: "can
        // production logs be located from this repo at all?" The answer is no whenever any
        // config resolves the path through an Octopus variable, which is the normal case.
        // Keying the flag off the chosen prefix instead would make it permanently false,
        // because every repo also ships a dev config with a real relative path.
        return (usablePrefix ?? tokenPrefix,
                tokenPrefix is not null,
                patterns.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static string? ReadOtelName(string repoDir)
    {
        foreach (string path in EnumerateFiles(repoDir, "appsettings.json"))
        {
            // Test projects carry their own appsettings with the same section; the src one wins.
            if (path.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? name = ReadOtelServiceName(ReadShared(path));
            if (name is not null)
            {
                return name;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern) =>
        Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(p => !ExcludedDirSegments.Any(seg => p.Contains(seg, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Read-only, share-everything. Hard constraint 12: nothing in the evidence workspace is
    /// ever opened for writing, and FileShare.ReadWrite matters because a running service
    /// holds its own files open.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
