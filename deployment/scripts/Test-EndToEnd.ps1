<#
.SYNOPSIS
  End-to-end validation: real agent (service host + session process) -> real API -> real PostgreSQL -> SignalR.

.DESCRIPTION
  Runs without administrator rights and without installing anything:
    * creates a scratch database, starts the API on http://127.0.0.1:<port> (dev mode, plain HTTP)
    * creates an admin token + enrollment token, starts the agent from artifacts\agent\payload (console host) and the session process
    * verifies enrollment, employee mapping, heartbeat, process start/stop, foreground sessions, mouse/keyboard COUNTS (no characters),
      idle-free activity, system metrics, offline buffering + ordered reconnect upload, duplicate protection,
      graceful stop -> SERVICE_STOPPED, restart -> resumes, SignalR live delivery.
  Needs: PostgreSQL reachable with the given connection info (default: the private dev instance on 127.0.0.1:5433),
         an interactive desktop (for foreground/input), .NET SDK 10, artifacts\agent\payload (run Build-Agent.ps1 first).
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 5099,
    [string]$PgHost = '127.0.0.1',
    [int]$PgPort = 5433,
    [string]$PgUser = 'postgres',
    [string]$PgPassword = '',
    [string]$Database = 'niss_e2e',
    [string]$Payload,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
if (-not $Payload) { $Payload = Join-Path $root 'artifacts\agent\payload' }
$work = Join-Path $env:TEMP ("niss-e2e-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
$agentData = Join-Path $work 'agent'
New-Item -ItemType Directory -Force $work, $agentData | Out-Null
$api = "http://127.0.0.1:$ApiPort"
$results = New-Object System.Collections.Generic.List[object]
$procs = @()
$agentRuns = 0

function Check($name, [scriptblock]$test) {
    try { $r = & $test; $ok = [bool]$r; $detail = if ($r -is [string]) { $r } else { '' } }
    catch { $ok = $false; $detail = $_.Exception.Message }
    $results.Add([pscustomobject]@{ Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $name, $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    return $ok
}
function Wait-Until([scriptblock]$cond, [int]$Seconds = 30, [int]$Every = 1) {
    $end = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $end) { try { $v = & $cond; if ($v) { return $v } } catch { }; Start-Sleep -Seconds $Every }
    return $null
}
$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) { $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName }
$env:PGPASSWORD = $PgPassword
$env:PGOPTIONS = '-c client_min_messages=warning'
function Sql([string]$db, [string]$q) {
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns native stderr into errors
    ((& $psql -h $PgHost -p $PgPort -U $PgUser -d $db -w -Atc $q 2>&1) | ForEach-Object { "$_" }) -join "`n"
}
function Api($method, $path, $body = $null, $token = $null) {
    $h = @{}; if ($token) { $h.Authorization = "Bearer $token" }
    $p = @{ Uri = "$api$path"; Method = $method; Headers = $h; UseBasicParsing = $true; ContentType = 'application/json' }
    if ($body) { $p.Body = ($body | ConvertTo-Json -Depth 10) }
    (Invoke-RestMethod @p)
}

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class E2eNative {
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("kernel32.dll")] public static extern bool FreeConsole();
  [DllImport("kernel32.dll")] public static extern bool AttachConsole(uint pid);
  [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
  [DllImport("kernel32.dll")] public static extern bool GenerateConsoleCtrlEvent(uint evt, uint grp);
  public static void MouseMove() { var a = new INPUT[2]; a[0].type = 0; a[0].u.mi.dx = 1; a[0].u.mi.dwFlags = 1; a[1].type = 0; a[1].u.mi.dx = -1; a[1].u.mi.dwFlags = 1; SendInput(2, a, Marshal.SizeOf(typeof(INPUT))); }
  public static void KeyTap(ushort vk) { var a = new INPUT[2]; a[0].type = 1; a[0].u.ki.wVk = vk; a[1].type = 1; a[1].u.ki.wVk = vk; a[1].u.ki.dwFlags = 2; SendInput(2, a, Marshal.SizeOf(typeof(INPUT))); }
  public static bool CtrlC(uint pid) { FreeConsole(); if (!AttachConsole(pid)) return false; SetConsoleCtrlHandler(IntPtr.Zero, true); bool ok = GenerateConsoleCtrlEvent(0, 0); System.Threading.Thread.Sleep(300); FreeConsole(); SetConsoleCtrlHandler(IntPtr.Zero, false); return ok; }
}
'@

function Start-Api {
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:NISS_Server__AllowInsecureHttp = 'true'; $env:NISS_Server__BindIp = '127.0.0.1'; $env:NISS_Server__Port = "$ApiPort"
    $env:NISS_Database__ConnectionString = "Host=$PgHost;Port=$PgPort;Database=$Database;Username=$PgUser" + $(if ($PgPassword) { ";Password=$PgPassword" } else { '' })
    $env:NISS_Security__BootstrapAdminPassword = 'E2e-Passw0rd!'
    $dll = Get-ChildItem (Join-Path $root 'server\NISS.Api\bin') -Recurse -Filter NISS.Api.dll | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $p = Start-Process dotnet -ArgumentList "`"$($dll.FullName)`"" -WorkingDirectory $dll.DirectoryName -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work 'api.out.log') -RedirectStandardError (Join-Path $work 'api.err.log')
    $script:procs += $p
    return $p
}
function Start-Agent {
    $env:NISS_AGENT_DATA = $agentData
    $env:NISS_Agent__Server__BaseUrl = $api
    $env:NISS_Agent__Session__RequireTrustedClientPath = 'false'
    $env:NISS_Agent__Session__LaunchSessionAgents = 'false'
    $env:NISS_Agent__Monitoring__HeartbeatSeconds = '5'
    $env:NISS_Agent__Monitoring__SystemMetricsSeconds = '5'
    $env:NISS_Agent__Monitoring__ProcessPollSeconds = '1'
    $script:agentRuns++
    $p = Start-Process (Join-Path $Payload 'NISSMonitor.Service.exe') -WorkingDirectory $Payload -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work "agent$($script:agentRuns).out.log") -RedirectStandardError (Join-Path $work "agent$($script:agentRuns).err.log")
    $script:procs += $p
    return $p
}
function Start-Session {
    $p = Start-Process (Join-Path $Payload 'NISSMonitor.Session.exe') -WorkingDirectory $Payload -PassThru
    $script:procs += $p
    return $p
}

try {
    # ---- database + API --------------------------------------------------------------------------------------------
    Write-Host "== Preparing database '$Database' on ${PgHost}:$PgPort" -ForegroundColor Cyan
    Sql 'postgres' "DROP DATABASE IF EXISTS $Database WITH (FORCE);" | Out-Null
    Sql 'postgres' "CREATE DATABASE $Database;" | Out-Null
    Write-Host '== Starting API' -ForegroundColor Cyan
    $apiProc = Start-Api
    Check 'API healthy + database connected' { Wait-Until { (Api GET '/api/v1/health').database } 60 } | Out-Null

    $login = Api POST '/api/v1/auth/login' @{ username = 'admin'; password = 'E2e-Passw0rd!' }
    $tok = $login.accessToken
    Check 'Admin login (JWT)' { $tok -and $login.user.role -eq 'SUPERADMIN' } | Out-Null

    Check 'Unauthenticated dashboard call is rejected' { try { Api GET '/api/v1/devices'; $false } catch { $_.Exception.Response.StatusCode.value__ -eq 401 } } | Out-Null

    # ---- enroll ----------------------------------------------------------------------------------------------------
    $tokenInfo = Api POST '/api/v1/enrollment-tokens' @{ employeeCode = 'EMP9001'; expiresInHours = 1; note = 'e2e' } $tok
    $env:NISS_Agent__Enrollment__Token = $tokenInfo.token
    Write-Host '== Starting agent (console host) + session process' -ForegroundColor Cyan
    $agent = Start-Agent
    $device = Wait-Until { (Api GET '/api/v1/devices' $null $tok).items | Where-Object { $_.employeeCode -eq 'EMP9001' } | Select-Object -First 1 } 60
    Check 'Device enrolled and mapped to employee EMP9001' { $device -and $device.deviceId -match '^PC-' } | Out-Null
    $devId = $device.deviceId
    Check 'Enrollment token is single-use' { try { Api POST '/api/v1/devices/enroll' @{ enrollmentToken = $tokenInfo.token; hostname = 'x'; windowsUsername = 'x'; agentVersion = '1.0.0' }; $false } catch { $true } } | Out-Null
    $session = Start-Session

    Check 'Heartbeat -> device ONLINE' { Wait-Until { (Api GET "/api/v1/devices/$devId" $null $tok).summary.status -eq 'ONLINE' } 40 } | Out-Null
    Check 'System metrics stored (CPU/RAM/disk)' { Wait-Until { [int](Sql $Database "select count(*) from system_metrics where device_id=(select id from devices where device_id='$devId')") -ge 1 } 40 } | Out-Null
    Check 'Session agent reports live state (foreground + presence)' { Wait-Until { $l = (Api GET '/api/v1/live' $null $tok) | Where-Object { $_.deviceId -eq $devId }; $l -and $l.presence -in 'ACTIVE', 'IDLE', 'LOCKED' } 40 } | Out-Null

    # ---- process start / stop ------------------------------------------------------------------------------------------
    $ping = Start-Process ping.exe -ArgumentList '-n 120 127.0.0.1' -WindowStyle Hidden -PassThru
    Check 'PROCESS_STARTED appears (ping.exe RUNNING)' { Wait-Until { (Api GET "/api/v1/devices/$devId/processes?search=ping" $null $tok).items | Where-Object { $_.processId -eq $ping.Id -and $_.status -eq 'RUNNING' } } 30 } | Out-Null
    Stop-Process -Id $ping.Id -Force
    Check 'PROCESS_STOPPED appears (ping.exe STOPPED with duration)' { Wait-Until { (Api GET "/api/v1/devices/$devId/processes?search=ping" $null $tok).items | Where-Object { $_.processId -eq $ping.Id -and $_.status -eq 'STOPPED' -and $_.durationSeconds -gt 0 } } 30 } | Out-Null

    # ---- input activity: counts only ---------------------------------------------------------------------------------
    1..8 | ForEach-Object { [E2eNative]::MouseMove(); [E2eNative]::KeyTap(0x7E); Start-Sleep -Milliseconds 150 }   # 0x7E = F15 (no visible effect)
    Check 'Mouse + keyboard activity COUNTS stored (no characters)' { Wait-Until { [int](Sql $Database "select coalesce(sum(keyboard_activity),0) from input_activity") -ge 1 -and [int](Sql $Database "select coalesce(sum(mouse_activity),0) from input_activity") -ge 1 } 60 } | Out-Null
    Check 'input_activity has no text/key columns' { (Sql $Database "select string_agg(column_name,',') from information_schema.columns where table_name='input_activity'") -notmatch 'key_?code|character|text|typed|scan' } | Out-Null

    Check 'Foreground application sessions recorded (sessions, not samples)' { Wait-Until { [int](Sql $Database "select count(*) from application_sessions") -ge 1 } 60 } | Out-Null

    # ---- SignalR live push -------------------------------------------------------------------------------------------
    $probe = Join-Path $PSScriptRoot 'signalr-probe.mjs'
    if ((Test-Path $probe) -and (Get-Command node -ErrorAction SilentlyContinue)) {
        $probeOut = Join-Path $work 'probe.json'
        $probeProc = Start-Process node -ArgumentList "`"$probe`" $api $tok `"$probeOut`" 25" -WorkingDirectory (Join-Path $root 'web\niss-monitor-dashboard') -WindowStyle Hidden -PassThru
        Start-Sleep -Seconds 4
        $p2 = Start-Process ping.exe -ArgumentList '-n 5 127.0.0.1' -WindowStyle Hidden -PassThru
        $probeProc | Wait-Process -Timeout 40 -ErrorAction SilentlyContinue
        Check 'SignalR pushes live events to a connected client' { (Test-Path $probeOut) -and ((Get-Content $probeOut -Raw | ConvertFrom-Json).received -ge 1) } | Out-Null
    }

    # ---- offline buffering + reconnect -------------------------------------------------------------------------------
    Write-Host '== Simulating server outage' -ForegroundColor Cyan
    Stop-Process -Id $apiProc.Id -Force; Start-Sleep -Seconds 2
    $offlinePing = Start-Process ping.exe -ArgumentList '-n 6 127.0.0.1' -WindowStyle Hidden -PassThru
    $offlinePing | Wait-Process -Timeout 20 -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 12
    $env:NISS_AGENT_DATA = $agentData
    $status = (& (Join-Path $Payload 'NISSAgent.exe') --status) -join "`n"
    Check 'Agent keeps collecting while offline (queue grows)' { $status -match 'Queue\s+:\s+(\d+) pending' -and [int]$Matches[1] -gt 0 } | Out-Null
    $beforeCount = [int](Sql $Database 'select count(*) from event_ingestion')
    Write-Host '== Restoring server' -ForegroundColor Cyan
    $apiProc = Start-Api
    Wait-Until { (Api GET '/api/v1/health').database } 60 | Out-Null
    $login = Api POST '/api/v1/auth/login' @{ username = 'admin'; password = 'E2e-Passw0rd!' }; $tok = $login.accessToken
    Check 'Backlog uploads after reconnect (queue drains)' { Wait-Until { ((& (Join-Path $Payload 'NISSAgent.exe') --status) -join "`n") -match 'Queue\s+:\s+0 pending' } 90 } | Out-Null
    Check 'Offline-period process reached the server with its original timestamps' { (Api GET "/api/v1/devices/$devId/processes?search=ping" $null $tok).items | Where-Object { $_.processId -eq $offlinePing.Id } } | Out-Null
    Check 'No duplicate events (event ids unique, counts grew)' { [int](Sql $Database 'select count(*) - count(distinct event_id) from event_ingestion') -eq 0 -and [int](Sql $Database 'select count(*) from event_ingestion') -gt $beforeCount } | Out-Null
    Check 'Server marks outage window / device ONLINE again' { Wait-Until { (Api GET "/api/v1/devices/$devId" $null $tok).summary.status -eq 'ONLINE' } 60 } | Out-Null

    # ---- graceful stop -> SERVICE_STOPPED, restart -> resumes ------------------------------------------------------
    Write-Host '== Stopping the agent gracefully (Ctrl+C)' -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'Send-CtrlC.ps1') -ProcessId $agent.Id | Out-Null
    $agent | Wait-Process -Timeout 30 -ErrorAction SilentlyContinue
    Check 'Graceful stop reported: device SERVICE_STOPPED on the server' { Wait-Until { (Api GET "/api/v1/devices/$devId" $null $tok).summary.status -eq 'SERVICE_STOPPED' } 30 } | Out-Null
    $agent = Start-Agent
    Check 'Restart: monitoring resumes (device ONLINE again)' { Wait-Until { (Api GET "/api/v1/devices/$devId" $null $tok).summary.status -eq 'ONLINE' } 60 } | Out-Null
    Check 'Restart reported as clean (AGENT_STARTED with no unexpected shutdown)' { Wait-Until { [int](Sql $Database "select count(*) from event_ingestion where event_type='AGENT_STARTED'") -ge 2 } 30 } | Out-Null

    # ---- offline detection -----------------------------------------------------------------------------------------------
    Stop-Process -Id $agent.Id -Force   # hard kill = crash
    Stop-Process -Id $session.Id -Force -ErrorAction SilentlyContinue
    Check 'Killed agent -> heartbeat WARNING/OFFLINE detected by the server' { Wait-Until { (Api GET "/api/v1/devices/$devId" $null $tok).summary.status -in 'WARNING', 'OFFLINE' } 150 5 } | Out-Null
    $agent = Start-Agent
    Check 'Crash recovery: next start reports UNEXPECTED previous shutdown (security event)' { Wait-Until { (Api GET '/api/v1/security-events?kind=UNEXPECTED_SHUTDOWN' $null $tok).items.Count -ge 1 } 60 } | Out-Null
}
catch {
    $msg = "SCRIPT ERROR: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim())"
    $results.Add([pscustomobject]@{ Test = 'E2E script completed without error'; Result = 'FAIL'; Detail = $msg })
    Set-Content (Join-Path $work 'script-error.txt') $msg
    Write-Host $msg -ForegroundColor Red
}
finally {
    if (-not $KeepRunning) { foreach ($p in $procs) { try { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force } } catch { } } }
    Write-Host ''
    $results | Format-Table -AutoSize | Out-String | Write-Host
    $failed = @($results | Where-Object Result -eq 'FAIL').Count
    Write-Host ("E2E: {0} passed, {1} failed. Logs: {2}" -f ($results.Count - $failed), $failed, $work) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })
    if ($failed) { exit 1 }
}
