<#
.SYNOPSIS
  Creates the HTTPS certificate for the central NISS server (LAN, self-signed) and wires it into the API configuration.

.DESCRIPTION
  * Certificate subject alternative names: localhost, 127.0.0.1, the computer name and every LAN IPv4 address of this PC
    (plus any -ExtraName you pass, e.g. the DNS name you use in agentsettings.json).
  * Exports server.pfx (random password) next to this script (git-ignored), and server.cer (public part).
  * Writes the certificate path/password into server\NISS.Api\serversettings.local.json (git-ignored).
  * Prints the SHA-256 thumbprint. Give it to the installer (--thumbprint) so agents *pin* this exact certificate,
    which lets a self-signed LAN certificate be validated strictly instead of disabling TLS validation.

  Does not need administrator rights (uses the current user's certificate store for creation only).
#>
[CmdletBinding()]
param(
    [string[]]$ExtraName = @(),
    [int]$ValidYears = 5,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Resolve-Path (Join-Path $here '..\..')
$pfx = Join-Path $here 'server.pfx'
$cer = Join-Path $here 'server.cer'
$local = Join-Path $root 'server\NISS.Api\serversettings.local.json'

if ((Test-Path $pfx) -and -not $Force) {
    Write-Host "A certificate already exists ($pfx). Use -Force to replace it." -ForegroundColor Yellow
} else {
    $ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress)
    $dns = @('localhost', $env:COMPUTERNAME) + $ExtraName | Where-Object { $_ } | Select-Object -Unique
    $sanParts = @($dns | ForEach-Object { "DNS=$_" }) + @($ips | ForEach-Object { "IPAddress=$_" })
    $san = $sanParts -join '&'

    Write-Host "Creating certificate for: $($sanParts -join ', ')"
    $cert = New-SelfSignedCertificate -Subject 'CN=NISS Monitor Server' -TextExtension @("2.5.29.17={text}$san", '2.5.29.37={text}1.3.6.1.5.5.7.3.1') `
        -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeyUsage DigitalSignature, KeyEncipherment `
        -NotAfter (Get-Date).AddYears($ValidYears) -CertStoreLocation 'Cert:\CurrentUser\My'

    Add-Type -AssemblyName System.Web
    $password = [System.Web.Security.Membership]::GeneratePassword(24, 4) -replace '[^A-Za-z0-9!#%*+=?]', 'x'
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -Force   # the PFX file is the source of truth; keep the store clean

    $settings = if (Test-Path $local) { Get-Content $local -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $settings.PSObject.Properties['Server']) { $settings | Add-Member Server ([pscustomobject]@{}) }
    $settings.Server | Add-Member Certificate ([pscustomobject]@{ Path = $pfx; Password = $password; Thumbprint = '' }) -Force
    $settings | ConvertTo-Json -Depth 8 | Set-Content $local -Encoding UTF8
    Write-Host "Wrote certificate settings to $local"
}

$loaded = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($cer)
$sha256 = ([System.BitConverter]::ToString($loaded.GetCertHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)) -replace '-', '')
Write-Host ''
Write-Host 'Certificate ready.' -ForegroundColor Green
Write-Host "  PFX (server) : $pfx"
Write-Host "  CER (public) : $cer"
Write-Host "  SHA-256 pin  : $sha256"
Write-Host ''
Write-Host 'Agents:    NISSMonitorSetup.exe --server https://<this-pc>:8443 --token <token> --thumbprint ' -NoNewline; Write-Host $sha256 -ForegroundColor Yellow
Write-Host 'Browsers:  import server.cer into "Trusted Root Certification Authorities" on each admin PC (or run Trust-NissCertificate.ps1 elevated) to avoid the warning.'
