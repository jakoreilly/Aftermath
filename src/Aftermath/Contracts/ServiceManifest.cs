namespace Aftermath.Contracts;

/// <summary>Everything the tool knows about one deployable service, all of it derived from
/// its clone with no network access. This is the join table described in the plan's Context:
/// Acme.Ledger.&lt;X&gt;.WebApi is simultaneously the NuGet package Octopus deploys, the
/// syslog identity, and the log filename prefix.</summary>
public sealed record ServiceManifest
{
    /// <summary>Directory name of the clone — the stable key used by every source.</summary>
    public required string Key { get; init; }

    public required string RepoPath { get; init; }

    /// <summary>From .gitlab-ci.yml `od_project_slug:`. Null when the repo has no Octopus
    /// project (NuGet libraries such as `shared`). Consumers must treat null as "deploys
    /// cannot be resolved for this service" rather than dereferencing it.</summary>
    public string? OctopusProjectSlug { get; init; }

    /// <summary>From .gitlab-ci.yml `.package-vars.package_name`. Doubles as the syslog
    /// identity and the log filename prefix.</summary>
    public string? PackageName { get; init; }

    /// <summary>From appsettings.json OpenTelemetrySettings:ServiceName, e.g.
    /// "lg_billingsvc". Not derivable from any other field — the estate has both "pt_" and
    /// "ACME_lg_" prefixes, so it must be read and left null when absent.</summary>
    public string? OtelServiceName { get; init; }

    /// <summary>From log4net.config &lt;file value=…&gt;. In a Release config this is the
    /// literal Octopus token "#{Acme.Logs.Path.And.File.Prefix}" and is NOT a path — see
    /// <see cref="LogPrefixIsToken"/>.</summary>
    public string? LogFilePrefix { get; init; }

    /// <summary>True when any of this service's log4net configs resolves the log path
    /// through an Octopus variable — i.e. the PRODUCTION log location is not knowable from
    /// the clone and the caller must supply --log-root. Note this is deliberately not
    /// "<see cref="LogFilePrefix"/> is a token": every repo also ships a dev config with a
    /// real relative path, so keying off the chosen prefix would leave the flag permanently
    /// false and hide the fact it exists to report.</summary>
    public bool LogPrefixIsToken { get; init; }

    /// <summary>Raw log4net conversionPattern strings found for this service. There are 21
    /// distinct variants estate-wide, so the parser is built per-service from these rather
    /// than from one hand-written regex.</summary>
    public IReadOnlyList<string> LogPatterns { get; init; } = [];
}
