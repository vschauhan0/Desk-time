<#
.SYNOPSIS
  Installs the NISS endpoint agent (service + session agent) from a payload folder or zip. Run elevated.

.DESCRIPTION
  PowerShell equivalent of NISSMonitorSetup.exe for managed rollouts (Intune, GPO startup script, SCCM, RMM).
  Steps: verify Windows -> extract files -> data directory ACL -> configuration -> service + recovery -> session startup
         -> local SQLite DB -> enroll -> start -> validate.

.EXAMPLE
  .\Install.ps1 -Payload .\NISSMonitorPayload.zip -Server https://192.168.1.10:8443 -Token ABC123 -Thumbprint 3F2A...
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Payload,      # NISSMonitorPayload.zip or an extracted payload folder
    [Parameter(Mandatory)] [string]$Server,       # https://<central-pc>:8443
    [string]$Token,                               # one-time enrollment token from the dashboard
    [string]$Employee,                            # optional employee code, e.g. EMP1024
    [string]$Thumbprint,                          # SHA-256 (or SHA-1) thumbprint of the server certificate to pin
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'NISS\Monitor'),
    [string]$DataDir = (Join-Path $env:ProgramData 'NISS\Agent')
)

$ErrorActionPreference = 'Stop'
$svc = 'NISSMonitorService'
$display = 'NISS Employee Monitoring Service'
$desc = 'Authorized NISS endpoint telemetry service for managed organizational devices.'
# Mutual watchdog: this and the main service each watch the other and restart it if it stops (Task Manager,
# Services.msc, sc stop). Both remain ordinary, visible services - see docs\security.md.
$wdSvc = 'Watchdog'
$wdDisplay = 'Watchdog'
$wdDesc = 'Keeps the NISS Employee Monitoring Service running. Authorized NISS endpoint component for managed organizational devices.'

function Step($msg) { Write-Host "[..] $msg" -ForegroundColor Cyan }
function Ok($msg) { Write-Host "[ok] $msg" -ForegroundColor Green }

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (administrator) PowerShell.'
}

Step 'Verifying Windows version'
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 17763 -or -not [Environment]::Is64BitOperatingSystem) { throw "Unsupported Windows (build $build). Windows 10 1809 / Server 2019 x64 or newer is required." }
Ok "Windows build $build"

Step 'Stopping existing services and session agents'
# Stop the watchdog first - otherwise it notices the main service going down and restarts it mid-install.
if (Get-Service $wdSvc -ErrorAction SilentlyContinue) { Stop-Service $wdSvc -Force -ErrorAction SilentlyContinue; (Get-Service $wdSvc).WaitForStatus('Stopped', '00:00:15') }
if (Get-Service $svc -ErrorAction SilentlyContinue) { Stop-Service $svc -Force -ErrorAction SilentlyContinue; (Get-Service $svc).WaitForStatus('Stopped', '00:00:40') }
Get-Process 'Windows.RunSession', 'NISSMonitor.Session', 'NISSAgent' -ErrorAction SilentlyContinue | Stop-Process -Force
Ok 'Stopped'

Step "Installing files to $InstallDir"
$settingsPath = Join-Path $InstallDir 'agentsettings.json'
$keptSettings = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw } else { $null }
New-Item -ItemType Directory -Force $InstallDir | Out-Null
if (Test-Path -PathType Leaf $Payload) { Expand-Archive -Path $Payload -DestinationPath $InstallDir -Force }
else { Copy-Item -Path (Join-Path $Payload '*') -Destination $InstallDir -Recurse -Force }
if ($keptSettings) { Set-Content -Path $settingsPath -Value $keptSettings -Encoding UTF8 }   # never clobber site configuration
Ok 'Files installed'

Step "Creating $DataDir (SYSTEM + Administrators only)"
New-Item -ItemType Directory -Force (Join-Path $DataDir 'Logs') | Out-Null
# Deliberately NOT recursive (no /T): this installer's own log file already lives under $DataDir and is open for
# writing while this runs, and recursively re-touching an open file's ACL can corrupt it into an empty (deny-all)
# DACL. The top-level grant is enough - NTFS inheritance (OI)(CI) applies it to everything created from here on.
& icacls.exe $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'icacls failed' }
Ok 'Permissions applied'

Step 'Writing configuration'
$cfg = if (Test-Path $settingsPath) { Get-Content $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if (-not $cfg.PSObject.Properties['Agent']) { $cfg | Add-Member Agent ([pscustomobject]@{}) }
if (-not $cfg.Agent.PSObject.Properties['Server']) { $cfg.Agent | Add-Member Server ([pscustomobject]@{}) }
$cfg.Agent.Server | Add-Member BaseUrl $Server.TrimEnd('/') -Force
if ($Thumbprint) { $cfg.Agent.Server | Add-Member CertificateThumbprint $Thumbprint -Force }
$cfg | ConvertTo-Json -Depth 8 | Set-Content -Path $settingsPath -Encoding UTF8
Ok "Server $Server"

Step 'Installing Windows service with recovery actions'
$bin = Join-Path $InstallDir 'Windows.RunService.exe'
if (-not (Get-Service $svc -ErrorAction SilentlyContinue)) {
    & sc.exe create $svc binPath= "`"$bin`"" start= auto obj= LocalSystem DisplayName= "$display" | Out-Null
} else {
    & sc.exe config $svc binPath= "`"$bin`"" start= auto obj= LocalSystem DisplayName= "$display" | Out-Null
}
& sc.exe description $svc "$desc" | Out-Null
& sc.exe failure $svc reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe failureflag $svc 1 | Out-Null
$ev = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\NISS Monitor Service'
New-Item -Path $ev -Force | Out-Null
New-ItemProperty -Path $ev -Name EventMessageFile -Value '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $ev -Name TypesSupported -Value 7 -PropertyType DWord -Force | Out-Null
Ok 'Service registered (automatic start, restart on 1st/2nd/subsequent failures)'

Step 'Installing watchdog service'
$wdBin = Join-Path $InstallDir 'Windows.RunWatchdog.exe'
if (-not (Get-Service $wdSvc -ErrorAction SilentlyContinue)) {
    & sc.exe create $wdSvc binPath= "`"$wdBin`"" start= auto obj= LocalSystem DisplayName= "$wdDisplay" | Out-Null
} else {
    & sc.exe config $wdSvc binPath= "`"$wdBin`"" start= auto obj= LocalSystem DisplayName= "$wdDisplay" | Out-Null
}
& sc.exe description $wdSvc "$wdDesc" | Out-Null
& sc.exe failure $wdSvc reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
& sc.exe failureflag $wdSvc 1 | Out-Null
$evWd = 'HKLM:\SYSTEM\CurrentControlSet\Services\EventLog\Application\NISS Monitor Watchdog'
New-Item -Path $evWd -Force | Out-Null
New-ItemProperty -Path $evWd -Name EventMessageFile -Value '%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll' -PropertyType ExpandString -Force | Out-Null
New-ItemProperty -Path $evWd -Name TypesSupported -Value 7 -PropertyType DWord -Force | Out-Null
Ok 'Watchdog registered'

Step 'Registering session agent startup'
New-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'NISSMonitorSession' -Value "`"$(Join-Path $InstallDir 'Windows.RunSession.exe')`"" -PropertyType String -Force | Out-Null
Ok 'Run key set'

$cli = Join-Path $InstallDir 'NISSAgent.exe'
Step 'Creating local SQLite database'
& $cli --init; if ($LASTEXITCODE -ne 0) { throw 'NISSAgent --init failed' }

if ($Token) {
    Step 'Enrolling device'
    $enrollArgs = @('--enroll', '--server', $Server, '--token', $Token)
    if ($Employee) { $enrollArgs += @('--employee', $Employee) }
    if ($Thumbprint) { $enrollArgs += @('--thumbprint', $Thumbprint) }
    & $cli @enrollArgs; if ($LASTEXITCODE -ne 0) { throw 'Enrollment failed' }
} else { Write-Warning 'No -Token supplied: enroll later with  NISSAgent.exe --enroll --server <url> --token <token>' }

Step 'Starting services'
Start-Service $svc; (Get-Service $svc).WaitForStatus('Running', '00:00:40')
Start-Service $wdSvc; (Get-Service $wdSvc).WaitForStatus('Running', '00:00:20')
Ok 'Service + watchdog running'

Step 'Validating communication with the server'
$healthy = $false
foreach ($i in 1..6) { & $cli --health; if ($LASTEXITCODE -eq 0) { $healthy = $true; break }; if ($LASTEXITCODE -eq 4) { break }; Start-Sleep 4 }
if ($healthy) { Ok 'Server reachable and device credential accepted' } else { Write-Warning 'Server validation did not succeed - see NISSAgent.exe --status and docs\troubleshooting.md' }

Write-Host ''
& $cli --status
