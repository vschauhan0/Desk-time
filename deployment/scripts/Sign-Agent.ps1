#Requires -Version 5.1
<#
.SYNOPSIS
  Authenticode-sign the NISS agent binaries with your organisation's code-signing certificate, then verify.
  Signing lets you allow-list the agent with AppLocker/WDAC (layer 4) so it cannot be replaced or swapped out, and
  it stops Windows/SmartScreen/AV treating the installer as unknown. You supply the certificate; this never creates
  one and never transmits it.

.DESCRIPTION
  Signs every NISS executable (and the project's own DLLs) found in a payload/build folder. Point it at the staging
  folder produced by Build-Agent.ps1 BEFORE it is packaged into NISSMonitorSetup.exe, or at an extracted payload.
  Requires signtool.exe (Windows SDK) on PATH or in a standard SDK location.

.PARAMETER PayloadDir
  Folder containing the built binaries to sign.

.PARAMETER Thumbprint
  SHA-1 thumbprint of a code-signing certificate in Cert:\CurrentUser\My or Cert:\LocalMachine\My. Use this OR -PfxPath.

.PARAMETER PfxPath
  Path to a .pfx code-signing certificate. Prompts for the password unless -PfxPassword is given.

.PARAMETER TimestampUrl
  RFC-3161 timestamp server so signatures remain valid after the certificate expires. Defaults to DigiCert's.

.EXAMPLE
  .\Sign-Agent.ps1 -PayloadDir ..\..\artifacts\agent\payload -Thumbprint 1A2B3C...

.EXAMPLE
  .\Sign-Agent.ps1 -PayloadDir .\payload -PfxPath .\niss-codesign.pfx
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [Parameter(Mandatory)] [string]$PayloadDir,
    [Parameter(Mandatory, ParameterSetName = 'Store')] [string]$Thumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Pfx')] [string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')] [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PayloadDir)) { throw "Payload folder not found: $PayloadDir" }

function Find-SignTool {
    $onPath = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    $candidates = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin" `
        -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending
    if ($candidates) { return $candidates[0].FullName }
    throw 'signtool.exe not found. Install the Windows SDK (Signing Tools) or add signtool.exe to PATH.'
}

$signtool = Find-SignTool
Write-Host "[..] Using $signtool"

# The agent's own binaries. Framework DLLs in a self-contained publish do not need your signature for AppLocker
# publisher rules on the executables; sign them too only if your WDAC policy requires every DLL to be signed.
$targets = Get-ChildItem -Path $PayloadDir -Recurse -Include 'NISSMonitorSetup.exe', 'NISSAgent.exe', 'Windows.Run*.exe', 'NISS.*.dll' -File
if (-not $targets) { throw "No NISS binaries found under $PayloadDir" }

$common = @('sign', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/v')
if ($PSCmdlet.ParameterSetName -eq 'Store') {
    $signArgs = $common + @('/sha1', $Thumbprint)
} else {
    if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
    $signArgs = $common + @('/f', $PfxPath)
    if ($PfxPassword) { $signArgs += @('/p', $PfxPassword) }
}

$failed = 0
foreach ($f in $targets) {
    & $signtool @signArgs $f.FullName
    if ($LASTEXITCODE -ne 0) { Write-Warning "sign failed: $($f.Name)"; $failed++ }
}
if ($failed) { throw "$failed file(s) failed to sign." }

Write-Host "[..] Verifying signatures"
foreach ($f in $targets) {
    & $signtool verify /pa /v $f.FullName | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Warning "verify failed: $($f.Name)"; $failed++ }
}
if ($failed) { throw "$failed file(s) failed verification." }

Write-Host "[ok] Signed and verified $($targets.Count) file(s). Re-run Build-Agent.ps1 packaging if you signed a staging folder." -ForegroundColor Green
