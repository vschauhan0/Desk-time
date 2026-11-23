<#
.SYNOPSIS
  Starts the whole NISS central server on this PC with one command: checks dependencies and PostgreSQL, applies
  database migrations, starts the ASP.NET Core API and the Next.js dashboard, prints the LAN URLs and checks health.

.PARAMETER DbConnection
  Full Npgsql connection string. Default: whatever is in server\NISS.Api\serversettings(.local).json.
.PARAMETER DbPassword
  Convenience: password for the connection string in serversettings.json (overrides its Password=).
.PARAMETER CreateDatabase
  Create the database if it does not exist (needs psql and a role allowed to CREATE DATABASE).
.PARAMETER Rebuild
  Force a rebuild of the API and the dashboard.
.PARAMETER SkipDashboard
  Start only the API.
.PARAMETER Stop
  Stop a previously started server (same as NISS.Server.Stop.ps1).

.EXAMPLE
  .\NISS.Server.Start.ps1 -DbPassword 'my-postgres-password' -CreateDatabase
#>
[CmdletBinding()]
param(
    [string]$DbConnection,
    [string]$DbPassword,
    [switch]$CreateDatabase,
    [switch]$Rebuild,
    [switch]$SkipDashboard,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$api = Join-Path $root 'server\NISS.Api'
$web = Join-Path $root 'web\niss-monitor-dashboard'
$runDir = Join-Path $root '.run'
$logDir = Join-Path $root 'logs'
New-Item -ItemType Directory -Force $runDir, $logDir | Out-Null

if ($Stop) { & (Join-Path $root 'NISS.Server.Stop.ps1'); return }

function Info($m) { Write-Host "[..] $m" -ForegroundColor Cyan }
function Ok($m) { Write-Host "[ok] $m" -ForegroundColor Green }
function Warn($m) { Write-Host "[!!] $m" -ForegroundColor Yellow }
function Fail($m) { Write-Host "[XX] $m" -ForegroundColor Red; exit 1 }

# ---- HTTP helper that works on Windows PowerShell 5.1 and PowerShell 7 with a self-signed LAN certificate -----------
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
if ($PSVersionTable.PSVersion.Major -lt 6 -and -not ('NissTrustAll' -as [type])) {
    Add-Type @'
using System.Net; using System.Security.Cryptography.X509Certificates;
public class NissTrustAll : ICertificatePolicy { public bool CheckValidationResult(ServicePoint sp, X509Certificate c, WebRequest r, int p) { return true; } }
'@
    [Net.ServicePointManager]::CertificatePolicy = New-Object NissTrustAll
}
function Get-Http([string]$Url) {
    try {
        if ($PSVersionTable.PSVersion.Major -ge 6) { return Invoke-WebRequest -Uri $Url -UseBasicParsing -SkipCertificateCheck -TimeoutSec 5 }
        return Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
    } catch { return $null }
}

# ---- 1. dependencies -----------------------------------------------------------------------------------------------
Info 'Checking dependencies'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail '.NET SDK 10 not found (https://dotnet.microsoft.com/download).' }
if (-not ((& dotnet --list-sdks) -match '^10\.')) { Fail ".NET SDK 10.x is required. Installed: $((& dotnet --list-sdks) -join '; ')" }
Ok ".NET SDK $((& dotnet --version))"
if (-not $SkipDashboard) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { Fail 'Node.js 20+ not found (https://nodejs.org).' }
    $nodeMajor = [int]((& node --version).TrimStart('v').Split('.')[0])
    if ($nodeMajor -lt 20) { Fail "Node.js 20+ required (found $(& node --version))." }
    Ok "Node.js $(& node --version)"
}

# ---- settings ------------------------------------------------------------------------------------------------------
function Read-Json($p) { if (Test-Path $p) { Get-Content $p -Raw | ConvertFrom-Json } else { $null } }
$base = Read-Json (Join-Path $api 'serversettings.json')
$local = Read-Json (Join-Path $api 'serversettings.local.json')
function Setting($section, $key, $default) {
    foreach ($s in @($local, $base)) { if ($s -and $s.$section -and $null -ne $s.$section.$key -and "$($s.$section.$key)" -ne '') { return $s.$section.$key } }
    return $default
}
$apiPort = [int](Setting 'Server' 'Port' 8443)
$dashPort = [int](Setting 'Dashboard' 'Port' 3000)
$conn = if ($DbConnection) { $DbConnection } else { Setting 'Database' 'ConnectionString' 'Host=localhost;Port=5432;Database=niss_monitor;Username=postgres' }
if ($DbPassword) { $conn = ($conn -replace '(?i)Password=[^;]*;?', '').TrimEnd(';') + ";Password=$DbPassword" }
$env:NISS_Database__ConnectionString = $conn

# ---- 2. PostgreSQL -------------------------------------------------------------------------------------------------
Info 'Checking PostgreSQL'
$kv = @{}; foreach ($part in $conn.Split(';')) { if ($part -match '^\s*([^=]+)=(.*)$') { $kv[$Matches[1].Trim().ToLower()] = $Matches[2].Trim() } }
$dbHost = if ($kv.host) { $kv.host } else { 'localhost' }; $dbPort = if ($kv.port) { [int]$kv.port } else { 5432 }
$dbName = if ($kv.database) { $kv.database } else { 'niss_monitor' }; $dbUser = if ($kv.username) { $kv.username } else { 'postgres' }
try {
    $tcp = New-Object Net.Sockets.TcpClient
    $iar = $tcp.BeginConnect($dbHost, $dbPort, $null, $null)
    if (-not $iar.AsyncWaitHandle.WaitOne(3000) -or -not $tcp.Connected) { throw 'timeout' }
    $tcp.Close()
} catch { Fail "PostgreSQL is not reachable at ${dbHost}:${dbPort}. Start the PostgreSQL service or fix Database:ConnectionString." }
Ok "PostgreSQL listening on ${dbHost}:${dbPort}"

$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) { $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName }
if ($psql) {
    $env:PGPASSWORD = $kv.password; $env:PGCONNECT_TIMEOUT = '5'; $env:PGOPTIONS = '-c client_min_messages=warning'
    # Windows PowerShell 5.1 turns native stderr into terminating errors: run psql with errors tolerated and inspect the exit code.
    function Invoke-Psql([string]$Database, [string]$Sql) {
        $ErrorActionPreference = 'Continue'
        $out = (& $psql -h $dbHost -p $dbPort -U $dbUser -d $Database -w -Atc $Sql 2>&1 | ForEach-Object { "$_" }) -join "`n"
        return [pscustomobject]@{ Code = $LASTEXITCODE; Output = $out.Trim() }
    }
    $probe = Invoke-Psql $dbName 'select 1'
    if ($probe.Output -ne '1') {
        if ($CreateDatabase) {
            Info "Creating database $dbName"
            $create = Invoke-Psql 'postgres' "CREATE DATABASE `"$dbName`""
            if ($create.Code -ne 0) { Fail "Could not create the database: $($create.Output)`n     Check the PostgreSQL user/password (-DbPassword)." }
        } else {
            Fail "Cannot open database '$dbName' as '$dbUser': $($probe.Output)`n     Pass -DbPassword <pw>, fix the connection string, or use -CreateDatabase."
        }
    }
    Ok "Database '$dbName' is accessible"
} else { Warn 'psql not found - skipping the SQL connectivity probe (migrations will still report errors).' }

# ---- certificate ---------------------------------------------------------------------------------------------------
$certPath = Setting 'Server' 'Certificate' $null
$hasCert = $certPath -and $certPath.Path -and (Test-Path $certPath.Path)
$insecure = [bool](Setting 'Server' 'AllowInsecureHttp' $false)
if (-not $hasCert -and -not $insecure) {
    Info 'No HTTPS certificate configured - creating a self-signed LAN certificate'
    & (Join-Path $root 'deployment\certificates\New-NissServerCertificate.ps1')
    $local = Read-Json (Join-Path $api 'serversettings.local.json')
}
$scheme = if ($insecure -and -not $hasCert) { 'http' } else { 'https' }

# ---- 3. build + migrations -----------------------------------------------------------------------------------------
Info 'Building the API'
$buildArgs = @('build', $api, '-c', 'Release', '--nologo', '-v', 'q')
if ($Rebuild) { $buildArgs += '--no-incremental' }
& dotnet @buildArgs; if ($LASTEXITCODE -ne 0) { Fail 'API build failed.' }
$dll = Get-ChildItem (Join-Path $api 'bin\Release') -Recurse -Filter 'NISS.Api.dll' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { Fail 'NISS.Api.dll not found after build.' }

Info 'Applying database migrations'
Push-Location $dll.DirectoryName
try { & dotnet $dll.FullName --migrate; if ($LASTEXITCODE -ne 0) { Fail 'Database migration failed (see output above).' } } finally { Pop-Location }
Ok 'Database schema is up to date'

# ---- 4. start the API ----------------------------------------------------------------------------------------------
$pidApi = Join-Path $runDir 'api.pid'
if (Test-Path $pidApi) { $old = Get-Process -Id ([int](Get-Content $pidApi)) -ErrorAction SilentlyContinue; if ($old) { Warn 'API already running - restarting it'; $old | Stop-Process -Force } }
Info 'Starting the API'
$apiOut = Join-Path $logDir 'api-console.log'; $apiErr = Join-Path $logDir 'api-console.err.log'
$apiProc = Start-Process dotnet -ArgumentList "`"$($dll.FullName)`"" -WorkingDirectory $dll.DirectoryName -WindowStyle Hidden `
    -RedirectStandardOutput $apiOut -RedirectStandardError $apiErr -PassThru
$apiProc.Id | Set-Content $pidApi

# ---- LAN addresses -------------------------------------------------------------------------------------------------
$lan = @(Get-NetIPConfiguration -ErrorAction SilentlyContinue | Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } | ForEach-Object { $_.IPv4Address.IPAddress })
if (-not $lan) { $lan = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '169.254.*' -and $_.IPAddress -ne '127.0.0.1' } | Select-Object -ExpandProperty IPAddress) }
$primary = if ($lan) { $lan[0] } else { 'localhost' }

# ---- 5. dashboard --------------------------------------------------------------------------------------------------
if (-not $SkipDashboard) {
    if (-not (Test-Path (Join-Path $web 'node_modules'))) { Info 'Installing dashboard dependencies (npm ci)'; Push-Location $web; try { & npm ci --no-audit --no-fund; if ($LASTEXITCODE -ne 0) { Fail 'npm ci failed' } } finally { Pop-Location } }
    Set-Content (Join-Path $web 'public\runtime-config.json') (@{ apiBaseUrl = "${scheme}://${primary}:$apiPort" } | ConvertTo-Json) -Encoding UTF8
    if ($Rebuild -or -not (Test-Path (Join-Path $web '.next\BUILD_ID'))) {
        Info 'Building the dashboard (next build)'
        Push-Location $web; try { & npm run build; if ($LASTEXITCODE -ne 0) { Fail 'Dashboard build failed.' } } finally { Pop-Location }
    }
    $pidWeb = Join-Path $runDir 'web.pid'
    if (Test-Path $pidWeb) { $old = Get-Process -Id ([int](Get-Content $pidWeb)) -ErrorAction SilentlyContinue; if ($old) { Stop-Process -Id $old.Id -Force } }
    Info 'Starting the dashboard'
    $env:PORT = "$dashPort"
    $webProc = Start-Process cmd.exe -ArgumentList "/c npm run start -- -H 0.0.0.0 -p $dashPort" -WorkingDirectory $web -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $logDir 'web-console.log') -RedirectStandardError (Join-Path $logDir 'web-console.err.log') -PassThru
    $webProc.Id | Set-Content $pidWeb
}

# ---- 6. health checks ----------------------------------------------------------------------------------------------
Info 'Waiting for the services to answer'
$apiUrl = "${scheme}://localhost:$apiPort/api/v1/health"
$healthy = $false
foreach ($i in 1..40) { $r = Get-Http $apiUrl; if ($r -and $r.StatusCode -eq 200) { $healthy = $true; break }; if ($apiProc.HasExited) { break }; Start-Sleep 1 }
if ($healthy) { Ok "API healthy: $apiUrl" } else {
    Write-Host (Get-Content $apiOut -Tail 25 -ErrorAction SilentlyContinue | Out-String); Write-Host (Get-Content $apiErr -Tail 25 -ErrorAction SilentlyContinue | Out-String)
    Fail "API did not become healthy. Logs: $apiOut / $apiErr"
}
if (-not $SkipDashboard) {
    $webOk = $false
    foreach ($i in 1..40) { $r = Get-Http "http://localhost:$dashPort/"; if ($r -and $r.StatusCode -lt 500) { $webOk = $true; break }; Start-Sleep 1 }
    if ($webOk) { Ok "Dashboard healthy: http://localhost:$dashPort/" } else { Warn "Dashboard did not respond yet - see $(Join-Path $logDir 'web-console.err.log')" }
}

# first-run admin password: the API prints it once to its console (stderr). Capture it, show it to the operator,
# then overwrite it in the redirected log so no plaintext credential is left on disk.
$boot = $null
try {
    $fs = [IO.File]::Open($apiErr, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $bytes = New-Object byte[] $fs.Length; [void]$fs.Read($bytes, 0, $bytes.Length)
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        $m = [regex]::Match($text, '(?m)^\s*username:\s*(?<u>\S+)\s*?
\s*password:\s*(?<p>\S+)')
        if ($m.Success) {
            $boot = @{ User = $m.Groups['u'].Value; Password = $m.Groups['p'].Value }
            $pw = $m.Groups['p']; $prefixBytes = [Text.Encoding]::UTF8.GetByteCount($text.Substring(0, $pw.Index))
            $fs.Position = $prefixBytes; $mask = [Text.Encoding]::UTF8.GetBytes(('*' * [Text.Encoding]::UTF8.GetByteCount($pw.Value))); $fs.Write($mask, 0, $mask.Length); $fs.Flush()
        }
    } finally { $fs.Dispose() }
} catch { }
Write-Host ''
Write-Host '======================= NISS server is running =======================' -ForegroundColor Green
foreach ($ip in $lan) {
    Write-Host "  Dashboard : http://${ip}:$dashPort"
    Write-Host "  API       : ${scheme}://${ip}:$apiPort/api/v1/health"
}
Write-Host "  Agents    : NISSMonitorSetup.exe --server ${scheme}://${primary}:$apiPort --token <token from Devices > Enrollment tokens>"
if ($boot) {
    Write-Host ''; Write-Host '  First-run administrator (shown ONCE - change it under Settings > Admin users):' -ForegroundColor Yellow
    Write-Host "    username: $($boot.User)"; Write-Host "    password: $($boot.Password)"
}
Write-Host '  Stop with : .\NISS.Server.Stop.ps1'
Write-Host '======================================================================' -ForegroundColor Green
Write-Host 'Windows Firewall: allow inbound TCP ports' $apiPort 'and' $dashPort 'from your LAN (see docs\installation.md).'
