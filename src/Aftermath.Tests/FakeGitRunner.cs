namespace Aftermath.Tests;

using Aftermath.Sources;

/// <summary>
/// Replays verbatim git output captured from the real estate. Preferred over a Moq setup for
/// this seam because the assertions that matter are about the ARGUMENTS the source passes —
/// the lightweight-tag gotcha is a format-string bug, not a parsing bug — and this records
/// every invocation for inspection.
/// </summary>
internal sealed class FakeGitRunner : IGitRunner
{
    private readonly Func<IReadOnlyList<string>, GitResult> respond;

    public FakeGitRunner(Func<IReadOnlyList<string>, GitResult> respond, bool available = true)
    {
        this.respond = respond;
        this.Available = available;
    }

    public List<IReadOnlyList<string>> Invocations { get; } = [];

    public bool Available { get; }

    public string Executable => "git";

    public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(this.Available);

    public Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        this.Invocations.Add(args);
        return Task.FromResult(this.respond(args));
    }

    public static GitResult NotARepo() =>
        new(128, string.Empty, "fatal: not a git repository (or any of the parent directories): .git");

    public static bool IsTagRead(IReadOnlyList<string> args) => args.Contains("for-each-ref");

    public static bool IsCommitRead(IReadOnlyList<string> args) => args.Contains("log");

    public string FormatArgumentOf(Func<IReadOnlyList<string>, bool> predicate) =>
        this.Invocations.Single(predicate).Single(a => a.StartsWith("--format=", StringComparison.Ordinal));
}
