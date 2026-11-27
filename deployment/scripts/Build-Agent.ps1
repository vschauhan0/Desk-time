<#
.SYNOPSIS
  Builds the NISS endpoint agent, packages NISSMonitorSetup.exe, and emits the agent auto-update package.

.DESCRIPTION
  1. dotnet publish (self-contained, win-x64) of NISSMonitor.Service, NISSMonitor.Watchdog, NISSMonitor.Session and
     NISSAgent into one payload folder (so the watchdog's Infrastructure/SQLite/Drawing dependencies are present)
  2. zips the payload -> NISSMonitorPayload.zip
  3. publishes the installer as a single file with the payload embedded -> NISSMonitorSetup.exe
  4. emits the agent AUTO-UPDATE package: artifacts\agent\update\<version>.zip (the payload files + manifest.json) and
     update\release.json (the same manifest, standalone). The manifest matches NISS.Monitor.Protocol.ReleaseManifest:
     { version, minOs?, files:[{path, sha256}], watchdogChanged, notes? }. Every shipped file is listed with its SHA-256
     (the agent installs ONLY listed files). Paths use '/' separators.

  The update server and agent accept an update ONLY when every .exe/.dll in it is Authenticode-signed by the pinned
  publisher. Pass -SignThumbprint / -SignSubject / -SignPfxPath to sign in the right order (sign -> hash -> manifest ->
  zip -> sign manifest). Without one the package is UNSIGNED: fine for layout checks, REJECTED by the server.

  Output: artifacts\agent\  (NISSMonitorSetup.exe, NISSMonitorPayload.zip, payload\, update\)
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    # --- auto-update package ---
    [string]$Version,                 # x.y.z; default = <Version> in Directory.Build.props
    [string]$Notes,
    [string]$MinOs,
    [switch]$WatchdogChanged,         # v1: surfaced as a warning on the agent, NOT auto-applied (a watchdog change needs a reinstall)
    # --- signing (optional; pick ONE certificate selector; see Sign-Agent.ps1) ---
    [string]$SignThumbprint,
    [string]$SignSubject,
    [string]$SignPfxPath,
    [string]$SignPfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowUntrustedRoot       # LAB ONLY (self-signed test certificate)
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

if (-not $Version) {
    $props = [xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
    $Version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Version'): the agent's health gate compares Major.Minor.Build." }

$signing = [bool]($SignThumbprint -or $SignSubject -or $SignPfxPath)
$signArgs = @{ TimestampUrl = $TimestampUrl }
if ($SignThumbprint) { $signArgs.Thumbprint = $SignThumbprint }
elseif ($SignSubject) { $signArgs.Subject = $SignSubject }
elseif ($SignPfxPath) { $signArgs.PfxPath = $SignPfxPath; if ($SignPfxPassword) { $signArgs.PfxPassword = $SignPfxPassword } }
if ($AllowUntrustedRoot) { $signArgs.AllowUntrustedRoot = $true }
$signScript = Join-Path $PSScriptRoot 'Sign-Agent.ps1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK 10 is required (dotnet not found).' }

if (-not $SkipTests) {
    Step 'Running agent tests'
    Run dotnet @('test', (Join-Path $root 'src\NISS.Monitor.Tests'), '-c', $Configuration, '--nologo', '-v', 'q')
}

if (Test-Path $out) { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
New-Item -ItemType Directory -Force $payload | Out-Null

foreach ($proj in 'NISS.Monitor.Service', 'NISS.Monitor.Watchdog', 'NISS.Monitor.Session', 'NISS.Monitor.Cli') {
    Step "Publishing $proj"
    # Version/AssemblyVersion/FileVersion are stamped so the agent's reported version == the manifest version
    # (the update health gate compares them; a mismatch rolls every update back).
    Run dotnet @('publish', (Join-Path $root "src\$proj"), '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true',
        '-p:PublishSingleFile=false', "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
        '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $payload, '--nologo', '-v', 'q')
}

# The watchdog references NISS.Monitor.Infrastructure (SQLite, System.Drawing, ...). All projects publish into the same
# folder so its dependency closure should be present - prove it rather than assume it, because an updated/installed
# watchdog with a missing DLL would simply fail to start on the device.
Step 'Checking the watchdog dependency closure'
$wdDeps = Get-Content (Join-Path $payload 'Windows.RunWatchdog.deps.json') -Raw | ConvertFrom-Json
$missing = New-Object System.Collections.Generic.List[string]
foreach ($target in $wdDeps.targets.PSObject.Properties) {
    foreach ($lib in $target.Value.PSObject.Properties) {
        foreach ($section in 'runtime', 'native') {
            if ($lib.Value.PSObject.Properties[$section]) {
                foreach ($asset in $lib.Value.$section.PSObject.Properties) {
                    if (-not (Test-Path (Join-Path $payload (Split-Path $asset.Name -Leaf)))) { $missing.Add($asset.Name) }
                }
            }
        }
    }
}
if ($missing.Count) { throw "Watchdog dependencies missing from the payload: $($missing -join ', ')" }
Write-Host "    ok ($((Get-ChildItem $payload -Filter *.dll).Count) dlls in payload)"

if ($signing) {
    Step 'Signing payload binaries (every .exe/.dll)'
    & $signScript -PayloadDir $payload @signArgs
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
if ($signing) {
    Step 'Signing NISSMonitorSetup.exe'
    & $signScript -FilePath (Join-Path $out 'NISSMonitorSetup.exe') @signArgs
}

# ---- auto-update package: <version>.zip (payload files + manifest.json) and release.json ---------------------------
Step "Building update package $Version"
$updDir = Join-Path $out 'update'
New-Item -ItemType Directory -Force $updDir | Out-Null
$payloadRoot = (Resolve-Path $payload).Path.TrimEnd('\') + '\'
# Site configuration is never shipped in a signed package: agentsettings*.json holds the per-site publisher pin, server
# URL and TLS pin, and a release must neither overwrite nor hash it.
$files = @(Get-ChildItem $payload -Recurse -File | Where-Object { $_.Name -notlike 'agentsettings*.json' } | Sort-Object FullName)
$entries = New-Object System.Collections.Generic.List[object]
foreach ($f in $files) {
    $rel = $f.FullName.Substring($payloadRoot.Length).Replace('\', '/')
    $entries.Add([ordered]@{ path = $rel; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $f.FullName).Hash })
}
$manifest = [ordered]@{ version = $Version }
if ($MinOs) { $manifest.minOs = $MinOs }
$manifest.files = $entries.ToArray()
$manifest.watchdogChanged = [bool]$WatchdogChanged
if ($Notes) { $manifest.notes = $Notes }
$json = $manifest | ConvertTo-Json -Depth 5
$utf8 = New-Object System.Text.UTF8Encoding($false)
$releaseJson = Join-Path $updDir 'release.json'
[System.IO.File]::WriteAllText($releaseJson, $json, $utf8)

# System.IO.Compression, not Compress-Archive: Windows PowerShell 5.1 writes backslash entry names; the server expects '/'.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$pkg = Join-Path $updDir "$Version.zip"
$za = [System.IO.Compression.ZipFile]::Open($pkg, 'Create')
try {
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($payloadRoot.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
    $e = $za.CreateEntry('manifest.json', [System.IO.Compression.CompressionLevel]::Optimal)
    $w = $e.Open()
    try { $b = $utf8.GetBytes($json); $w.Write($b, 0, $b.Length) } finally { $w.Dispose() }
} finally { $za.Dispose() }

if ($signing) {
    Step 'Signing release.json (detached .p7s)'
    & $signScript -ManifestPath $releaseJson @signArgs
}

$size = '{0:N1} MB' -f ((Get-Item (Join-Path $out 'NISSMonitorSetup.exe')).Length / 1MB)
$pkgSize = '{0:N1} MB' -f ((Get-Item $pkg).Length / 1MB)
Write-Host ''
Write-Host "Done. $out" -ForegroundColor Green
Write-Host "  NISSMonitorSetup.exe   ($size)"
Write-Host "  update\$Version.zip    ($($entries.Count) files, $pkgSize) - upload as SUPERADMIN (see docs/agent-updates-operations.md)"
if (-not $signing) { Write-Warning 'Package is UNSIGNED: the server will reject it. Re-run with -SignThumbprint / -SignSubject / -SignPfxPath.' }
Write-Host '  Install (elevated):    NISSMonitorSetup.exe --server https://<central-pc>:8443 --token <one-time-token> [--thumbprint <sha256>]'
