# Starts a local Loki + Grafana instance for aggregating Aftermath's MCP-server
# logs. Native Windows binaries, not Docker (installing Docker Desktop needs
# admin rights + WSL2/Hyper-V + a reboot - too invasive to assume).
#
# Downloads the binaries into bin/ on first run (gitignored - large and
# machine-specific). Idempotent: checks each PID file against a LIVE,
# matching-image process before starting a second one that would only fail to
# bind the same port and leave a zombie behind.

$ErrorActionPreference = "Stop"
$obsDir = $PSScriptRoot
$binDir = Join-Path $obsDir "bin"
$grafanaVersion = "13.2.0"   # bump after checking github.com/grafana/grafana/releases/latest
$grafanaHome = Join-Path $binDir "grafana-$grafanaVersion"
$lokiExe = Join-Path $binDir "loki-windows-amd64.exe"

New-Item -ItemType Directory -Force -Path $binDir | Out-Null

if (-not (Test-Path $lokiExe)) {
    Write-Output "Downloading Loki..."
    $asset = (Invoke-RestMethod "https://api.github.com/repos/grafana/loki/releases/latest").assets |
        Where-Object { $_.name -eq "loki-windows-amd64.exe.zip" }
    $zip = Join-Path $binDir "loki.zip"
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip
    Expand-Archive $zip -DestinationPath $binDir -Force
    Remove-Item $zip
    Write-Output "  Loki $($asset.name) -> $lokiExe"
}

if (-not (Test-Path (Join-Path $grafanaHome "bin\grafana.exe"))) {
    Write-Output "Downloading Grafana $grafanaVersion..."
    $zip = Join-Path $binDir "grafana.zip"
    Invoke-WebRequest "https://dl.grafana.com/oss/release/grafana-$grafanaVersion.windows-amd64.zip" -OutFile $zip
    Expand-Archive $zip -DestinationPath $binDir -Force
    Remove-Item $zip
    Write-Output "  Grafana -> $grafanaHome"
}

function Test-AlreadyRunning($pidFile, $expectedProcessName) {
    if (-not (Test-Path $pidFile)) { return $false }
    $existingId = Get-Content $pidFile -ErrorAction SilentlyContinue
    if (-not $existingId) { return $false }
    $proc = Get-Process -Id $existingId -ErrorAction SilentlyContinue
    return ($proc -and $proc.ProcessName -eq $expectedProcessName)
}

function Start-Hidden($exe, $argsLine, $workDir, $pidFile) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = $argsLine
    $psi.WorkingDirectory = $workDir
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $proc = [System.Diagnostics.Process]::Start($psi)
    $proc.Id | Out-File $pidFile -Encoding ascii
    return $proc.Id
}

$lokiPidFile = Join-Path $obsDir "loki.pid"
if (Test-AlreadyRunning $lokiPidFile "loki-windows-amd64") {
    Write-Output "Loki already running, pid=$(Get-Content $lokiPidFile)."
} else {
    $lokiCfg = Join-Path $obsDir "config\loki-config.yaml"
    $lokiPid = Start-Hidden $lokiExe "-config.file=`"$lokiCfg`"" $binDir $lokiPidFile
    Write-Output "Loki started, pid=$lokiPid, http://localhost:3100 (~15s until /ready stops saying 'Ingester not ready')"
}

$grafanaPidFile = Join-Path $obsDir "grafana.pid"
if (Test-AlreadyRunning $grafanaPidFile "grafana") {
    Write-Output "Grafana already running, pid=$(Get-Content $grafanaPidFile)."
} else {
    $grafanaCfg = Join-Path $obsDir "config\grafana-custom.ini"
    $grafanaPid = Start-Hidden (Join-Path $grafanaHome "bin\grafana.exe") "server --homepath=`"$grafanaHome`" --config=`"$grafanaCfg`"" $grafanaHome $grafanaPidFile
    Write-Output "Grafana started, pid=$grafanaPid, http://localhost:3000 (admin/admin) - 20-40s to load plugins before it answers /api/health"
}

Write-Output ""
Write-Output "Once both are up, provision the Loki datasource (one time):"
Write-Output '  curl -s -u admin:admin -X POST http://localhost:3000/api/datasources -H "Content-Type: application/json" -d ''{"name":"Loki","type":"loki","access":"proxy","url":"http://localhost:3100","isDefault":true}'''
Write-Output ""
Write-Output "Then run the MCP server with logging pointed at Loki:"
Write-Output '  $env:INCIDENTTIMELINE_LOKI_URL = "http://localhost:3100"'
Write-Output "  (its logs appear in Grafana Explore under  {app=\"aftermath\"} )"
