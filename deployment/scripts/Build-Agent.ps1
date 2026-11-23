<#
.SYNOPSIS
  Builds the NISS endpoint agent and packages NISSMonitorSetup.exe.

.DESCRIPTION
  1. dotnet publish (self-contained, win-x64) of NISSMonitor.Service, NISSMonitor.Session and NISSAgent into one payload folder
  2. zips the payload -> NISSMonitorPayload.zip
  3. publishes the installer as a single file with the payload embedded -> NISSMonitorSetup.exe

  Output: artifacts\agent\  (NISSMonitorSetup.exe, NISSMonitorPayload.zip, payload\)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$out = Join-Path $root 'artifacts\agent'
$payload = Join-Path $out 'payload'
$zip = Join-Path $out 'NISSMonitorPayload.zip'

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Run($file, $arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK 10 is required (dotnet not found).' }

if (-not $SkipTests) {
    Step 'Running agent tests'
    Run dotnet @('test', (Join-Path $root 'src\NISS.Monitor.Tests'), '-c', $Configuration, '--nologo', '-v', 'q')
}

if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null

foreach ($proj in 'NISS.Monitor.Service', 'NISS.Monitor.Watchdog', 'NISS.Monitor.Session', 'NISS.Monitor.Cli') {
    Step "Publishing $proj"
    Run dotnet @('publish', (Join-Path $root "src\$proj"), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $payload, '--nologo', '-v', 'q')
}

Step 'Creating payload archive'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal -Force

Step 'Publishing NISSMonitorSetup.exe (single file, payload embedded)'
$setupDir = Join-Path $out 'setup'
Run dotnet @('publish', (Join-Path $root 'src\NISS.Monitor.Installer'), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None', '-p:DebugSymbols=false', "-p:PayloadZip=$zip", '-o', $setupDir, '--nologo', '-v', 'q')
Copy-Item (Join-Path $setupDir 'NISSMonitorSetup.exe') $out -Force
Remove-Item $setupDir -Recurse -Force

$size = '{0:N1} MB' -f ((Get-Item (Join-Path $out 'NISSMonitorSetup.exe')).Length / 1MB)
Write-Host ''
Write-Host "Done. $out" -ForegroundColor Green
Write-Host "  NISSMonitorSetup.exe   ($size)"
Write-Host '  Install (elevated):    NISSMonitorSetup.exe --server https://<central-pc>:8443 --token <one-time-token> [--thumbprint <sha256>]'
