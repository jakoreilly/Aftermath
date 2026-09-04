namespace Aftermath.Cli;

/// <summary>
/// Parses "--flag value" / "--switch" pairs from the args following the verb.
/// Pure — no I/O — so it's directly unit-testable.
/// </summary>
public static class ArgParser
{
    public static Dictionary<string, string?> Parse(IReadOnlyList<string> args)
    {
        var flags = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key = token[2..];
            bool hasValue = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            flags[key] = hasValue ? args[++i] : "true";
        }

        return flags;
    }

    public static string? Get(this Dictionary<string, string?> flags, string key) =>
        flags.TryGetValue(key, out string? v) ? v : null;

    public static bool GetBool(this Dictionary<string, string?> flags, string key, bool defaultValue = false) =>
        flags.TryGetValue(key, out string? v) ? !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase) : defaultValue;

    public static int GetInt(this Dictionary<string, string?> flags, string key, int defaultValue) =>
        flags.TryGetValue(key, out string? v) && int.TryParse(v, out int parsed) ? parsed : defaultValue;
}
