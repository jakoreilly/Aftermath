# Local log aggregation — Loki + Grafana

A throwaway local instance for making the **MCP server's** own logs queryable in
Grafana. Native Windows binaries, no Docker. Not a production deployment: no auth
hardening, no TLS, loopback only.

Only the MCP-server path logs here. The one-shot CLI (`run.cmd collect` etc.)
still writes plain text to stderr and is unaffected.

## Layout

| Path | In git? | What |
|---|---|---|
| `config/loki-config.yaml` | yes | single-node, filesystem-backed Loki on `:3100` |
| `config/grafana-custom.ini` | yes | Grafana OSS on `:3000`, `admin`/`admin` |
| `start.ps1` / `stop.ps1` | yes | start/stop both, hidden, PID-file tracked |
| `bin/` | no | binaries, downloaded by `start.ps1` on first run |
| `data/` | no | each service's own state |

## Run it

```powershell
observability\start.ps1
```

First run downloads Loki (latest) and Grafana (pinned in `start.ps1`) into
`bin/`. Loki needs ~15s before `/ready` stops reporting "Ingester not ready";
Grafana needs 20-40s to load plugins before it answers `/api/health`.

One-time, once Grafana is up — provision the Loki datasource:

```
curl -s -u admin:admin -X POST http://localhost:3000/api/datasources \
  -H "Content-Type: application/json" \
  -d '{"name":"Loki","type":"loki","access":"proxy","url":"http://localhost:3100","isDefault":true}'
```

## Point the MCP server at it

The Loki sink is **off unless `INCIDENTTIMELINE_LOKI_URL` is set** — an
unconfigured run behaves exactly as before and opens no socket (hard constraint
1). With it set, logs ship to Loki under the label `{app="aftermath"}` *and*
still go to stderr.

```powershell
$env:INCIDENTTIMELINE_LOKI_URL = "http://localhost:3100"
# then launch the MCP server (no args), or set the var in .mcp.json's "env"
```

In Grafana: **Explore → Loki → `{app="aftermath"}`**.

## Verified end to end (2026-09-04, against the running instance)

1. Synthetic line `POST /loki/api/v1/push` → `GET /query_range` round-trips. ✓
2. MCP server run with `INCIDENTTIMELINE_LOKI_URL` set, driven through a stdio
   `initialize` + `tools/list` probe: stdout stayed clean JSON-RPC, its Serilog
   lines (`initialize`/`tools/list` handlers, startup, shutdown) came back from a
   direct Loki query for `{app="aftermath"}`. ✓
3. Querying through the **Grafana datasource proxy** was not re-run here — the
   shared local Grafana's admin password is no longer `admin`/`admin`. The
   Grafana↔Loki path on this machine's instance was confirmed previously and the
   Loki datasource is provisioned; check `{app="aftermath"}` in Explore to
   confirm on your side.

## If this should become permanent

Standing this up locally is not a decision about how the project's observability
runs for real. A serious step (a hosted collector, retention policy, non-default
credentials, dashboards as code) is a separate infrastructure call.
