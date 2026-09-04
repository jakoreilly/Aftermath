# Aftermath

Assembles a provenance-labelled incident timeline from local git clones, log files and
(later) Octopus Deploy and DbExplorer.

**Read-only and offline by default.** It opens no database, holds no credential, writes to
no remote system, and never opens a file in the evidence workspace for writing. A default
run completes with the network cable unplugged.

This repository's README documents the design decisions directly; the original phase-by-phase
implementation plan (with local development-machine paths) is kept out of the public copy.


## Build and test

```
dotnet build src/Aftermath.slnx
dotnet test  src/Aftermath.Tests/Aftermath.Tests.csproj
```

`run.cmd` builds and then runs, for a plain Windows shell — `run.cmd` on its own prints the
tool's help, and anything after it is passed through:

```
run.cmd collect --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --window 24h
```

It launches the built `.exe` rather than `dotnet run`, which installs its own Ctrl-C handling
and would sit between the console and the tool's cancellation path.

## Current state — Phases 1 and 2 complete

`services` builds the cross-system join table from a workspace of clones. Nothing in the
Acme estate carries a correlation key that spans git, Octopus, the log files and
OpenTelemetry — but every join is derivable from the clone, at a fixed key, with no network:

```
run.cmd services --workspace c:\workspace\work
```

```json
{
  "key": "billing-service",
  "octopusProjectSlug": "ledger-billing-service",
  "packageName": "Acme.Ledger.BillingService.WebApi",
  "otelServiceName": "lg_billingsvc",
  "logFilePrefix": "logs\\Acme.Ledger.BillingService.WebApi_",
  "logPrefixIsToken": true,
  "logPatterns": [ "%date{HH:mm:ss.fff} [%property{AcmeLogPrefix}] ..." ]
}
```

`Acme.Ledger.<X>.WebApi` is the pivot: it is simultaneously the NuGet package Octopus
deploys, the syslog `identity`, and the log filename prefix. `octopusProjectSlug` bridges to
Octopus; `trace_id` in the log lines bridges across services.

`logPrefixIsToken: true` means the **production** log path resolves through an Octopus
variable (`#{Acme.Logs.Path.And.File.Prefix}`) and is therefore not knowable from the
clone — the caller must supply `--log-root`. That is the normal case, not an error.

### `collect` — evidence around an incident

`collect` windows every registered source around an instant and prints one JSON
`SourceResult` per source. Phase 2 registers the git source: release tags, commits on `HEAD`
and `CHANGELOG.md` entries, all from the local clones. The payload is an **array** — Phases 3
and 7 add sources to the same verb — so the object below is its single element, abridged to
one of the 21 events.

```
run.cmd collect --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --window 24h
```

```json
{
  "sourceName": "git",
  "status": "Ok",
  "message": "Read 26 of 29 clones; 2 release(s) and 19 commit(s) in window. Not git working copies, so nothing was read from them: Acme-shared-infrastructure-testing-master, Acme.shared-master, shared-master.",
  "events": [
    {
      "at": "2026-07-17T12:19:21+00:00",
      "kind": "Release",
      "confidence": "Observed",
      "service": "core-service",
      "summary": "Released v1.15.0",
      "provenance": "core-service@v1.15.0",
      "tickets": [ "INC-7337" ],
      "detail": "lightweight tag on commit 5b4c9eb; core-service/CHANGELOG.md:1 — record virtual zone Id. Closes Jira ticket INC-7337"
    }
  ]
}
```

Three properties hold for every event, and the tests enforce all three:

- **`at` is always UTC.** A single repo carries commits stamped `Z`, `+01:00` *and* `+02:00`;
  they are normalised at the parse, never later. `--at` without a zone is rejected rather
  than assumed.
- **`provenance` is always resolvable by a human** — a git ref, or a `file:line`.
- **`confidence` is set by the source that produced it and never upgraded.** `Observed` is a
  git object on disk. `Reported` here means a `CHANGELOG.md` heading that no tag carries:
  those record a *date with no time of day*, so the event is pinned to midnight UTC and
  carries a `Caveat` saying so, rather than inventing an instant.

A source **never throws**. If `git` is missing from the host's `PATH` the result comes back
`Skipped` with the remedy in `message`, and the run still exits 0 — a partial timeline with
a named gap is useful during an incident; a stack trace is not. That is what lets Phase 7
add the unreachable sources without touching anything upstream.

Scale, for reference: `--window 8760h` (a year, 26 clones, 78 `git` invocations) returns
2,985 events — 147 releases and 2,838 commits — in **6.9 s** including start-up.

### The `logs` source — log4net files, `Error.txt`, HTTP status/latency

Production's own log directory can never be discovered from a clone — every Release config
resolves it through an Octopus variable, `#{Acme.Logs.Path.And.File.Prefix}` — so this
source is **`Skipped`**, not guessed at, until the operator copies logs locally and passes
`--log-root`:

```
run.cmd collect --workspace c:\workspace\work --log-root C:\incident\logs --at 2026-07-17T13:00:00Z
```

A log4net line carries no date at all — every pattern in the estate is `%date{HH:mm:ss.fff}`,
time only — and its timestamp is host-local with no offset, in a zone that observes DST
(`--timezone`, default `Europe/Dublin`). The date comes from the filename; a file that rolls
past midnight holds two calendar days, and every event after that carry is marked
`Confidence.Inferred` with a caveat rather than silently trusting the filename. `services'`
`logPatterns` is a *candidate set*, not one grammar — a service's file and syslog appenders
can order the same fields differently, so the source tries each candidate against a file's
first line and keeps the winner, falling back to a tolerant parser (and `Confidence.Inferred`)
when none of them recognise it.

Three things come out of the same log text with no metrics store and no network:

- **`LogWarning` / `LogError` / `LogFatal` events**, clustered — repeated stack traces from the
  same failure collapse into one event with a count, and every distinct `trace_id` seen at
  Error-or-above gets its own cross-cutting event, since one request can carry several
  different error messages across the same trace.
- **`ServiceStop` events from `Error.txt`** — the only record of a startup failure, written
  when log4net itself never came up. Two writer styles matter: appended (a crash history
  survives) versus overwritten (only the latest crash does).
- **`HttpMetrics` events**, one per service per minute — request count, 4xx, 5xx, p50/p95
  latency, recovered from `SharedHttpLoggingMiddleware`'s fixed `HTTP Response: …` line.
  `accountservice` and `billing-service` use a different middleware whose source isn't in the
  workspace; those get an explicit **`"HTTP metrics unavailable for this service"`** event
  instead of a silent zero, because a zero that means "not parsed" reads exactly like a zero
  that means "outage".

Every secret this source could otherwise surface — the `AcmeLogPrefix`/`SessionID` field is
a live session token on web services — is carried through as `correlationPrefix` until it
reaches `Redactor`, the single point where it is pseudonymised before anything is rendered.

### `draft` — the postmortem document

`draft` merges every source into one de-duplicated, totally-ordered `Timeline`
(`Correlation/TimelineBuilder`), ranks what changed nearby (`SuspectRanker` — proximity and
coincidence only; nothing here is named `Cause`, `RootCause` or `Culprit`, and the tool never
asserts one), and renders the fixed-template document (`Rendering/TemplateNarrator`):

```
run.cmd draft --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --out draft.md
```

Six sections, always in this order: **What we looked at**, **What we could not see**,
**Changed nearby**, **Error rate and latency**, **Timeline** (a mermaid `timeline` diagram,
then the full event table), **Open questions for the reviewer**. Empty states are fixed copy
— "No evidence found in the window…", "Nothing changed in this window…" — chosen so a quiet
window reads as evidence of quiet, not as a broken tool.

**Redaction is not optional and cannot be disabled.** Every event is redacted
(`Redactor.RedactEvent`) before it reaches the page, and the fully rendered document is
redacted once more (`Redactor.Apply`) as a final safety net at the single output boundary —
the same call that caught a real bug during development: a Release event's own provenance,
`"core-service@v1.14.0"`, matched the email pattern's original catch-all TLD and came out as
`[EMAIL]` until the pattern was tightened. Order matters: correlation ids (the session token,
account ids) are pseudonymised with a stable per-run hash *first*, so grouping survives, then
passwords, bearer tokens, DbExplorer tokens, emails, Irish VRMs, Luhn-validated PANs and IBANs
are redacted. A fallback-parsed event's correlation field is blanked wholesale rather than
pseudonymised, because its role was never established.

## MCP — the AI inclusion, without an AI dependency

This tool calls no model at all (Goal G): it exposes five deterministic, offline-by-default
tools, and the MCP host — Claude or any other client — becomes the narrator. Copy
[`.mcp.json.example`](.mcp.json.example) to `.mcp.json`, set `INCIDENTTIMELINE_WORKSPACE` (and
optionally `_LOG_ROOT` / `_TIMEZONE`), and restart the host. A stdio session receives no
command-line arguments — `Program.cs` starts the MCP server whenever it is launched with none,
and falls back to the one-shot CLI whenever a human passes some directly.

| Tool | Returns |
|---|---|
| `incident_services` | the service join table |
| `incident_collect` | raw `SourceResult`s for a window (capped at 500 events, with an explicit truncation note) |
| `incident_timeline` | the ordered, de-duplicated timeline — its description carries the narration contract verbatim: evidence, not analysis; cite Provenance; never state a cause |
| `incident_suspects` | ranked changes (never asserted as causes), with scores and evidence |
| `incident_draft` | the fully redacted markdown document |

Every tool takes `workspace`/`logRoot`/`timezone` as optional per-call overrides on top of the
env-configured defaults, so one long-lived MCP session can still investigate a different
workspace or a different copied-logs directory per call without a restart.

Verify a server by hand with a raw stdio probe (`initialize`, then `tools/list` /
`tools/call`) rather than trusting the unit suite alone for this front door — that is exactly
how Phase 6's one real defect was found: `incident_draft` hung indefinitely on its first live
call. `git` never had `RedirectStandardInput` set, so every `git` child process inherited the
MCP server's *own* stdin — the host's JSON-RPC pipe — and blocked on it. No exception, no
timeout, nothing a unit test could see, because no unit test runs inside an MCP host's own
process — the bug was found by probing the MCP server live with raw stdio, not by the unit suite.

## The network sources — opt-in, behind `--online`

Octopus deploys, DbExplorer diagnostics, GitLab CI pipelines and GitHub Actions runs are all
real evidence sources, but every one of them opens a socket, so all of them are opt-in behind
`--online` (hard constraint 1) — the default run still completes with the network cable
unplugged, and each source individually Skips when its own URL/token isn't configured, so
partial credentials degrade per source rather than all-or-nothing:

```
run.cmd draft --workspace c:\workspace\work --at 2026-07-17T13:00:00Z --online ^
  --octopus-url https://deploy.acme.example --octopus-token API-XXXX ^
  --gitlab-url https://bull.acme.example --gitlab-token glpat-XXXX ^
  --github-token ghp-XXXX
```

| Source | Adds | Confidence | Verified live? |
|---|---|---|---|
| `octopus` | `Deploy` events, joined on `ServiceManifest.OctopusProjectSlug` | `Reported` | Endpoint reachable (a genuine Octopus 401 with no token) — fixture-based for lack of a token, not a network path |
| `dbexplorer` | `DbBlocking`/`DbDeadlock` events, estate-wide (`Service = "*"`) | `Reported` | No runtime URL discoverable anywhere in this workspace — fixture-only; only the 403/Profiler-scope degradation is confirmed |
| `gitlab` | `CiPipeline` events, failures only | `Reported` | **Built against a live response** — `bull.acme.example` answered real project/pipeline data behind this machine's TLS-inspecting proxy; Phase 0's original "unreachable" verdict most likely mis-read that proxy's handshake failure as GitLab rejecting the request |
| `github` | `CiPipeline` events, failed workflow runs only | `Reported` | **Not probed** — DTOs built from GitHub's documented REST v3 shape for `GET /repos/{owner}/{repo}/actions/runs`, not a captured response; tests run against a hand-written fixture of that shape |

`gitlab` and `github` each resolve a clone's own project by reading its `origin` remote (the
same `IGitRunner` seam `git` already uses) rather than guessing a path — always accurate,
since it comes from the clone itself. `github` keys off `--github-token` (or
`INCIDENTTIMELINE_GITHUB_TOKEN`); the base URL defaults to `https://api.github.com` and only
a GitHub Enterprise Server host needs `--github-url`.

Adding every one of these sources changed **zero files** under `Correlation/` or `Rendering/`
— the abstraction proof Phase 7 exists for — and `draft` with no `--online` produces output
identical to before these sources existed, given the same inputs (verified with `git
worktree` against the `v0.6-preflight` tag).

## Roadmap

| Phase | Scope | Status |
|---|---|---|
| 1 | Repo, contracts, service join table | **done** |
| 2 | Git and release evidence (offline) | **done** |
| 3 | log4net file evidence, `Error.txt`, HTTP status/latency | **done** |
| 4 | Correlation and suspect ranking | **done** |
| 5 | Redaction, rendering, the postmortem document | **done** |
| 6 | MCP front door — the LLM narrates through it | **done** |
| 7 | Octopus / DbExplorer / GitLab behind the source interface | **done** |

All seven phases are complete: 238 tests, 0 build warnings, every hard-constraint grep clean.
