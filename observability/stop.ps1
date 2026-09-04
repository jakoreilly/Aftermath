# Stops the local Loki + Grafana instance started by start.ps1.
$obsDir = $PSScriptRoot
foreach ($name in @("loki", "grafana")) {
    $pidFile = Join-Path $obsDir "$name.pid"
    if (Test-Path $pidFile) {
        $procId = Get-Content $pidFile
        Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue
        Remove-Item $pidFile -ErrorAction SilentlyContinue
        Write-Output "Stopped $name (pid $procId)"
    }
}
