<#
.SYNOPSIS
  Repairs an installed NISS endpoint agent: re-applies service registration + recovery actions, data-directory
  permissions and the session startup entry, verifies the database, and restarts the service. Run elevated.

.DESCRIPTION
  Safe to run at any time; it keeps the device identity, queue and configuration. Optionally re-installs the binaries
  from a payload (e.g. to fix a corrupted file). To re-enroll a device use a re-enroll token from the dashboard:
      NISSAgent.exe --enroll --server <url> --token <token>
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'NISS\Monitor'),
    [string]$Payload                      # optional: NISSMonitorPayload.zip or folder to restore binaries from
)

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (administrator) PowerShell.'
}

$svc = 'NISSMonitorService'
$wdSvc = 'Watchdog'
if ($Payload) {
    Write-Host '[..] Restoring binaries from payload'
    # Watchdog first - otherwise it notices the main service stop and restarts it mid-repair.
    if (Get-Service $wdSvc -ErrorAction SilentlyContinue) { Stop-Service $wdSvc -Force -ErrorAction SilentlyContinue; (Get-Service $wdSvc).WaitForStatus('Stopped', '00:00:15') }
    if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force -ErrorAction SilentlyContinue; (Get-Service $svc).WaitForStatus('Stopped', '00:00:40') }
    Get-Process 'NISSMonitor.Session' -ErrorAction SilentlyContinue | Stop-Process -Force
    $settings = Join-Path $InstallDir 'agentsettings.json'
    $kept = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }
    if (Test-Path -PathType Leaf $Payload) { Expand-Archive -Path $Payload -DestinationPath $InstallDir -Force } else { Copy-Item (Join-Path $Payload '*') $InstallDir -Recurse -Force }
    if ($kept) { Set-Content $settings $kept -Encoding UTF8 }
}

$cli = Join-Path $InstallDir 'NISSAgent.exe'
if (-not (Test-Path $cli)) { throw "NISSAgent.exe not found in $InstallDir - run Install.ps1 instead." }

& $cli --repair
& $cli --health
Write-Host ''
& $cli --status
