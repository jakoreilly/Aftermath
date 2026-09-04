namespace Aftermath.Discovery;

using System.Text.Json;
using System.Text.RegularExpressions;
using Aftermath.Contracts;

/// <summary>
/// Builds the cross-system join table from a workspace of clones, with no network access.
/// Every extractor below takes file CONTENT rather than a path, so it is directly unit
/// testable; only <see cref="Scan"/> touches the disk.
/// </summary>
public static partial class WorkspaceScanner
{
    // od_project_slug: ledger-billing-service      (billing-service/.gitlab-ci.yml:11)
    [GeneratedRegex(
        @"^\s*od_project_slug:\s*(?<v>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex OdProjectSlug();

    //   package_name: Acme.Ledger.BillingService.WebApi   (billing-service/.gitlab-ci.yml:16)
    [GeneratedRegex(
        @"^\s*package_name:\s*(?<v>\S+)\s*$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageName();

    // <file value="logs\Acme.Ledger.BillingService.WebApi_"/>
    [GeneratedRegex(
        @"<file\s+value\s*=\s*""(?<v>[^""]*)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LogFileValue();

    // The common form: <conversionPattern value="%date{HH:mm:ss.fff} ..."/>
    [GeneratedRegex(
        @"<conversionPattern\s+value\s*=\s*""(?<v>[^""]*)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex ConversionPattern();

    // The second form, used by every RemoteSyslogAppender in the estate — the pattern is an
    // attribute on the layout element itself rather than a child element:
    //   <layout type="log4net.Layout.PatternLayout" value="%date{HH:mm:ss.fff} ..."/>
    // Requiring the value to start with '%' is what distinguishes it from the type attribute.
    // Missing this form loses the format of the CENTRAL syslog copy — often the only log
    // available during an incident, and in at least one service (api-gateway2) its field
    // order differs from the file appender's.
    [GeneratedRegex(
        @"<layout\b[^>]*?\svalue\s*=\s*""(?<v>%[^""]*)""",
        RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex LayoutValuePattern();

    public static string? ReadOctopusSlug(string gitlabCiYaml) => First(OdProjectSlug(), gitlabCiYaml);

    public static string? ReadPackageName(string gitlabCiYaml) => First(PackageName(), gitlabCiYaml);

    public static string? ReadLogFilePrefix(string log4netXml) => First(LogFileValue(), log4netXml);

    public static IReadOnlyList<string> ReadLogPatterns(string log4netXml) =>
        ConversionPattern().Matches(log4netXml).Select(m => m.Groups["v"].Value)
            .Concat(LayoutValuePattern().Matches(log4netXml).Select(m => m.Groups["v"].Value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>An Octopus variable token, not a path. 23 of the estate's Release configs
    /// carry "#{Acme.Logs.Path.And.File.Prefix}" here.</summary>
    public static bool IsOctopusToken(string? value) =>
        value is not null && value.StartsWith("#{", StringComparison.Ordinal);

    /// <summary>
    /// Reads OpenTelemetrySettings:ServiceName. Uses a JSON parser rather than a regex
    /// because the value is nested. Returns null when the section or property is absent —
    /// gdpr-service has no OpenTelemetrySettings section at all, and a synthesised name
    /// would silently match no traces while looking like it worked.
    /// </summary>
    public static string? ReadOtelServiceName(string appSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(appSettingsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(
                appSettingsJson,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (doc.RootElement.ValueKind is not JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("OpenTelemetrySettings", out JsonElement otel) ||
                otel.ValueKind is not JsonValueKind.Object ||
                !otel.TryGetProperty("ServiceName", out JsonElement name) ||
                name.ValueKind is not JsonValueKind.String)
            {
                return null;
            }

            string? value = name.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (JsonException)
        {
            // A malformed appsettings.json is evidence about the repo, not a reason to fail
            // the whole scan. The manifest simply carries no OTel name.
            return null;
        }
    }

    private static string? First(Regex r, string input) =>
        string.IsNullOrEmpty(input)
            ? null
            : r.Match(input) is { Success: true } m ? m.Groups["v"].Value : null;
}
