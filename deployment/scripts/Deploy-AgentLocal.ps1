<#
.SYNOPSIS
  Deploys the freshly built agent payload to THIS PC's installed NISS agent (an in-place upgrade), keeping the device
  identity, queue and configuration. Self-elevates: run it from an ordinary shell and approve the UAC prompt.

.DESCRIPTION
  Wraps deployment\install\Repair.ps1 -Payload with the payload produced by Build-Agent.ps1, then prints the running
  binary timestamps and the session log tail so you can see the new agent came up. Use this for local dev/test
  upgrades; production endpoints are upgraded with NISSMonitorSetup.exe or Install.ps1/Repair.ps1 from a share.
#>
[CmdletBinding()]
param(
    [string]$Payload = (Join-Path $PSScriptRoot '..\..\artifacts\agent\NISSMonitorPayload.zip'),
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'NISS\Monitor')
)

$ErrorActionPreference = 'Stop'
$repair = Join-Path $PSScriptRoot '..\install\Repair.ps1'
$payloadFull = (Resolve-Path $Payload).Path

# Self-elevate: re-launch this script elevated if we are not already an administrator.
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting administrator rights (approve the UAC prompt)...' -ForegroundColor Yellow
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Payload', $payloadFull, '-InstallDir', $InstallDir)
    Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $args -Verb RunAs
    return
}

Write-Host "Deploying payload: $payloadFull" -ForegroundColor Cyan
& $repair -Payload $payloadFull -InstallDir $InstallDir

Write-Host ''
Write-Host 'Installed binaries (should be today):' -ForegroundColor Cyan
Get-ChildItem $InstallDir -Filter 'NISS.Monitor.Core.dll' | Select-Object Name, LastWriteTime | Format-Table -AutoSize
Get-ChildItem $InstallDir -Filter 'Windows.RunSession.exe' | Select-Object Name, LastWriteTime | Format-Table -AutoSize

Write-Host 'Waiting for the service to relaunch the session agent...' -ForegroundColor Cyan
Start-Sleep -Seconds 12
$session = Get-Process 'Windows.RunSession' -ErrorAction SilentlyContinue
if ($session) {
    Write-Host ("Session agent running: PID {0}, started {1}" -f $session.Id, $session.StartTime) -ForegroundColor Green
} else {
    Write-Warning 'Session agent not running yet - it launches within a few seconds of the service start, or on next logon.'
}

$log = Join-Path $env:LOCALAPPDATA 'NISS\Session\session.log'
if (Test-Path $log) {
    Write-Host ''
    Write-Host 'Session log (tail):' -ForegroundColor Cyan
    Get-Content $log -Tail 8
}

Write-Host ''
Write-Host 'Done. Bring a flagged site to the foreground, then check the dashboard Screens tab (filter: Flagged).' -ForegroundColor Green
Write-Host 'Keep this window open to read the output.' -ForegroundColor DarkGray
pause
