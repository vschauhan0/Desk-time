<#
.SYNOPSIS  Stops the NISS API and dashboard started by NISS.Server.Start.ps1.
#>
$ErrorActionPreference = 'Continue'
$runDir = Join-Path $PSScriptRoot '.run'
foreach ($name in 'api', 'web') {
    $file = Join-Path $runDir "$name.pid"
    if (-not (Test-Path $file)) { continue }
    $procId = [int](Get-Content $file)
    $p = Get-Process -Id $procId -ErrorAction SilentlyContinue
    if ($p) {
        # kill the process tree (npm -> node)
        & taskkill.exe /PID $procId /T /F | Out-Null
        Write-Host "[ok] stopped $name (pid $procId)" -ForegroundColor Green
    }
    Remove-Item $file -Force -ErrorAction SilentlyContinue
}
