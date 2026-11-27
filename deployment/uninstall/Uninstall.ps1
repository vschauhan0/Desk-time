<#
.SYNOPSIS
  Removes the NISS endpoint agent. Run elevated. Administrators can always uninstall - nothing here is hidden or protected against them.

.PARAMETER Purge
  Also delete the local queue database, device identity and logs in C:\ProgramData\NISS\Agent.

.NOTES
  To stop *non-administrators* from removing the agent, use standard Windows policy (users have no rights to stop the
  service or delete files under Program Files by default). See docs\security.md "Preventing unauthorised removal".
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'NISS\Monitor'),
    [string]$DataDir = (Join-Path $env:ProgramData 'NISS\Agent'),
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'
$svc = 'NISSMonitorService'
$wdSvc = 'Watchdog'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (administrator) PowerShell.'
}

# If the services were hardened (STOP/DELETE denied to admins), restore default rights first. Admins keep WRITE_DAC
# in the hardened DACL, so this sdset succeeds; without it the Stop/delete below would fail with access denied.
$DefaultSddl = 'D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)'
foreach ($s in @($wdSvc, $svc)) { if (Get-Service $s -ErrorAction SilentlyContinue) { & sc.exe sdset $s $DefaultSddl | Out-Null } }

Write-Host '[..] Stopping services'
# Watchdog first - otherwise it notices the main service stop and restarts it mid-uninstall.
if (Get-Service $wdSvc -ErrorAction SilentlyContinue) {
    Stop-Service $wdSvc -Force -ErrorAction SilentlyContinue
    (Get-Service $wdSvc).WaitForStatus('Stopped', '00:00:15')
}
if (Get-Service $svc -ErrorAction SilentlyContinue) {
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
    (Get-Service $svc).WaitForStatus('Stopped', '00:00:40')
}
Write-Host '[..] Stopping session agents'
Get-Process 'NISSMonitor.Session' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host '[..] Removing service registration'
if (Get-Service $wdSvc -ErrorAction SilentlyContinue) { & sc.exe delete $wdSvc | Out-Null }
if (Get-Service $svc -ErrorAction SilentlyContinue) { & sc.exe delete $svc | Out-Null }
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'NISSMonitorSession' -ErrorAction SilentlyContinue

Write-Host '[..] Removing program files'
if (Test-Path $InstallDir) {
    for ($i = 0; $i -lt 5; $i++) {
        try { Remove-Item $InstallDir -Recurse -Force -ErrorAction Stop; break } catch { Start-Sleep 1 }
    }
    if (Test-Path $InstallDir) { Write-Warning "Some files in $InstallDir are still in use; they can be deleted after a restart." }
}

if ($Purge) {
    Write-Host '[..] Deleting local data'
    if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force -ErrorAction SilentlyContinue }
} else {
    Write-Host "[ok] Local data kept in $DataDir (use -Purge to delete)"
}
Write-Host '[ok] NISS Monitor agent uninstalled. The device record remains on the server; revoke it in the dashboard (Devices > Revoke).' -ForegroundColor Green
