<#
.SYNOPSIS
  Trusts the NISS server certificate on this PC (adds server.cer to Trusted Root). Run elevated on each PC whose
  browser opens the dashboard. Endpoint agents do not need this - they pin the certificate by thumbprint.
#>
[CmdletBinding()]
param([string]$Cer = (Join-Path $PSScriptRoot 'server.cer'))

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (administrator) PowerShell.'
}
if (-not (Test-Path $Cer)) { throw "Certificate file not found: $Cer" }
Import-Certificate -FilePath $Cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
Write-Host "Trusted: $Cer" -ForegroundColor Green
