#Requires -Version 5.1
<#
.SYNOPSIS
  Authenticode-sign the NISS agent binaries (and the update manifest) with your organisation's code-signing
  certificate, then verify with Get-AuthenticodeSignature. You supply the certificate (an internal-CA / ADCS
  code-signing cert is the intended one); this never creates one and never transmits it. Nothing is hardcoded:
  the certificate is chosen by -Thumbprint, -Subject or -PfxPath.

.DESCRIPTION
  Three things can be signed in one run:
    -PayloadDir     every .exe and .dll under the folder (recursive). The update server and the agent require EVERY
                    .exe/.dll in an update package to carry the pinned publisher's signature (the leaf subject must
                    match AcceptedPublisherSubjects), including the self-contained .NET runtime DLLs - so this
                    re-signs them all by default. -OwnBinariesOnly restores the old narrow set (NISS binaries only),
                    which is enough for AppLocker/WDAC publisher rules but NOT for an update package.
    -FilePath       explicit files (e.g. NISSMonitorSetup.exe).
    -ManifestPath   release.json. JSON cannot carry an Authenticode signature, so a detached CMS/PKCS#7 signature is
                    written next to it as <name>.p7s and verified. The agent trusts the manifest through its per-file
                    SHA-256 + the signed binaries it lists; the .p7s is the publisher's tamper-evident attestation of
                    the exact manifest (for audit / out-of-band verification), it is not read by the v1 agent.

  Order matters: Authenticode changes a file's bytes, so sign BEFORE hashing. Build-Agent.ps1 does this for you
  (sign payload -> hash -> manifest -> zip -> sign manifest); prefer its -Sign* parameters over running this by hand
  on a finished package. After signing, every file is verified; a signature that is not Valid fails the run.

.PARAMETER Thumbprint
  Certificate thumbprint in Cert:\CurrentUser\My or Cert:\LocalMachine\My.
.PARAMETER Subject
  Match a code-signing certificate in those stores by subject substring (e.g. 'CN=NISS Agent Publisher'). Must be
  unambiguous (newest valid cert wins if several match and -Subject is exact CN).
.PARAMETER PfxPath
  A .pfx file. Prompts for the password unless -PfxPassword is given.
.PARAMETER TimestampUrl
  RFC-3161 timestamp server so signatures outlive the certificate. Pass '' to skip (offline / lab).
.PARAMETER AllowUntrustedRoot
  LAB ONLY. Accept a signature whose chain is not trusted on THIS machine (e.g. self-signed test cert). Verification
  still requires a signature made by the chosen certificate. Never use for a release.

.EXAMPLE
  .\Sign-Agent.ps1 -PayloadDir ..\..\artifacts\agent\payload -Thumbprint 1A2B3C...
.EXAMPLE
  .\Sign-Agent.ps1 -ManifestPath ..\..\artifacts\agent\update\release.json -Subject 'CN=NISS Agent Publisher'
#>
[CmdletBinding(DefaultParameterSetName = 'Store')]
param(
    [string]$PayloadDir,
    [string[]]$FilePath,
    [string]$ManifestPath,
    [Parameter(Mandatory, ParameterSetName = 'Store')] [string]$Thumbprint,
    [Parameter(Mandatory, ParameterSetName = 'Subject')] [string]$Subject,
    [Parameter(Mandatory, ParameterSetName = 'Pfx')] [string]$PfxPath,
    [Parameter(ParameterSetName = 'Pfx')] [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$OwnBinariesOnly,
    [switch]$AllowUntrustedRoot
)

$ErrorActionPreference = 'Stop'

if (-not $PayloadDir -and -not $FilePath -and -not $ManifestPath) { throw 'Nothing to sign: pass -PayloadDir, -FilePath and/or -ManifestPath.' }
if ($PayloadDir -and -not (Test-Path $PayloadDir)) { throw "Payload folder not found: $PayloadDir" }
if ($ManifestPath -and -not (Test-Path $ManifestPath)) { throw "Manifest not found: $ManifestPath" }

function Get-SigningCert {
    switch ($PSCmdlet.ParameterSetName) {
        'Pfx' {
            if (-not (Test-Path $PfxPath)) { throw "PFX not found: $PfxPath" }
            $pw = if ($PfxPassword) { ConvertTo-SecureString $PfxPassword -AsPlainText -Force } else { Read-Host -AsSecureString "Password for $PfxPath" }
            return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @($PfxPath, $pw)
        }
        default {
            $all = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -ErrorAction SilentlyContinue | Where-Object { $_.HasPrivateKey })
            $now = Get-Date
            if ($PSCmdlet.ParameterSetName -eq 'Store') {
                $hit = @($all | Where-Object { $_.Thumbprint -eq ($Thumbprint -replace '\s', '').ToUpperInvariant() })
            } else {
                $hit = @($all | Where-Object { $_.Subject -like "*$Subject*" -and $_.NotBefore -le $now -and $_.NotAfter -ge $now } |
                    Sort-Object NotAfter -Descending)
            }
            if (-not $hit) { throw 'No matching code-signing certificate with a private key in CurrentUser\My / LocalMachine\My.' }
            return $hit[0]
        }
    }
}

$cert = Get-SigningCert
$now = Get-Date
if ($cert.NotAfter -lt $now -or $cert.NotBefore -gt $now) { throw "Certificate $($cert.Thumbprint) is not within its validity period." }
$eku = $cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' }
if (-not $eku) { throw "Certificate $($cert.Thumbprint) does not have the Code Signing EKU (1.3.6.1.5.5.7.3.3)." }
Write-Host "[..] Signing as $($cert.Subject)  [$($cert.Thumbprint)]"

# --- collect targets -------------------------------------------------------------------------------------------
$targets = New-Object System.Collections.Generic.List[string]
if ($PayloadDir) {
    $filter = if ($OwnBinariesOnly) { @('NISSMonitorSetup.exe', 'NISSAgent.exe', 'Windows.Run*.exe', 'NISS.*.dll') } else { @('*.exe', '*.dll') }
    foreach ($f in (Get-ChildItem -Path $PayloadDir -Recurse -Include $filter -File)) { $targets.Add($f.FullName) }
    if ($targets.Count -eq 0) { throw "No binaries found under $PayloadDir" }
}
foreach ($f in $FilePath) {
    if (-not (Test-Path $f)) { throw "File not found: $f" }
    $targets.Add((Resolve-Path $f).Path)
}

# --- sign + verify binaries ------------------------------------------------------------------------------------
$failed = 0
$sigArgs = @{ Certificate = $cert; HashAlgorithm = 'SHA256'; IncludeChain = 'notroot' }
if ($TimestampUrl) { $sigArgs.TimestampServer = $TimestampUrl }

foreach ($path in $targets) {
    $r = Set-AuthenticodeSignature -FilePath $path @sigArgs
    if (-not $r.SignerCertificate) { Write-Warning "sign failed: $path ($($r.StatusMessage))"; $failed++ }
}
if ($failed) { throw "$failed file(s) failed to sign." }

Write-Host "[..] Verifying $($targets.Count) signature(s) with Get-AuthenticodeSignature"
foreach ($path in $targets) {
    $v = Get-AuthenticodeSignature -FilePath $path
    $ours = $v.SignerCertificate -and ($v.SignerCertificate.Thumbprint -eq $cert.Thumbprint)
    $trusted = $v.Status -eq 'Valid'
    if (-not $ours -or (-not $trusted -and -not $AllowUntrustedRoot)) {
        Write-Warning "verify failed: $path  status=$($v.Status) $($v.StatusMessage)"
        $failed++
    }
}
if ($failed) { throw "$failed file(s) failed verification. (Chain not trusted on this machine? Install the internal CA root, or use -AllowUntrustedRoot for a lab run only.)" }

# --- detached CMS signature over release.json ------------------------------------------------------------------
if ($ManifestPath) {
    Add-Type -AssemblyName System.Security
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $ManifestPath).Path)
    $content = New-Object System.Security.Cryptography.Pkcs.ContentInfo -ArgumentList (, $bytes)
    $cms = New-Object System.Security.Cryptography.Pkcs.SignedCms -ArgumentList @($content, $true)   # detached
    $signer = New-Object System.Security.Cryptography.Pkcs.CmsSigner -ArgumentList @([System.Security.Cryptography.Pkcs.SubjectIdentifierType]::IssuerAndSerialNumber, $cert)
    $signer.DigestAlgorithm = New-Object System.Security.Cryptography.Oid -ArgumentList '2.16.840.1.101.3.4.2.1'   # SHA-256
    $signer.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::WholeChain
    $cms.ComputeSignature($signer)
    $sigPath = "$((Resolve-Path $ManifestPath).Path).p7s"
    [System.IO.File]::WriteAllBytes($sigPath, $cms.Encode())

    # verify: re-read both files from disk
    $check = New-Object System.Security.Cryptography.Pkcs.SignedCms -ArgumentList @((New-Object System.Security.Cryptography.Pkcs.ContentInfo -ArgumentList (, [System.IO.File]::ReadAllBytes($ManifestPath))), $true)
    $check.Decode([System.IO.File]::ReadAllBytes($sigPath))
    $check.CheckSignature($AllowUntrustedRoot.IsPresent)   # $true = signature math only; $false = also chain validation
    if ($check.SignerInfos[0].Certificate.Thumbprint -ne $cert.Thumbprint) { throw 'Manifest signature was not made by the selected certificate.' }
    Write-Host "[ok] Manifest signed: $sigPath"
}

if ($targets.Count) { Write-Host "[ok] Signed and verified $($targets.Count) binary file(s) as '$($cert.Subject)'." -ForegroundColor Green }
Write-Host "     Set AcceptedPublisherSubjects (server AND agent) to exactly: $($cert.Subject)"
