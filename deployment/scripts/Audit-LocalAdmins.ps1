#Requires -Version 5.1
<#
.SYNOPSIS
  Read-only audit: who can stop, disable or uninstall the NISS agent on this PC. Lists the local Administrators
  group and the NISS service state. Changes NOTHING. Safe to run as a standard user (fuller detail when elevated).

.DESCRIPTION
  Only a member of the local Administrators group can stop/disable/delete the NISS services or remove the files in
  Program Files. Taking local-admin rights away from employees is therefore the single most effective anti-tamper
  control there is - far stronger than anything the agent can do to defend itself, because a non-admin simply has no
  rights to touch it. See docs\least-privilege-rollout.md.

  This script only REPORTS who currently has that power; it never changes group membership. For each account that
  looks like an ordinary user it prints the exact (manual) command to remove it. Push it across the fleet with your
  RMM/Intune and collect the output - or just the exit code - to find every PC that still has an employee in the
  local Administrators group.

  Exit code: 0 = only expected admins (built-in Administrator / admin groups); 2 = user accounts found that should
  probably be removed; 1 = the group could not be read.

.EXAMPLE
  .\Audit-LocalAdmins.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

function Get-AdminGroupName {
    # Resolve by well-known SID so this works on non-English Windows too.
    try { return (Get-LocalGroup -SID 'S-1-5-32-544' -ErrorAction Stop).Name } catch { return 'Administrators' }
}

function Get-AdminMembers {
    param([string]$Group)
    try {
        return @(Get-LocalGroupMember -Group $Group -ErrorAction Stop | ForEach-Object {
                [pscustomobject]@{
                    Name        = $_.Name
                    Sid         = $(if ($_.SID) { $_.SID.Value } else { '' })
                    ObjectClass = $_.ObjectClass        # User / Group
                    Source      = $_.PrincipalSource    # Local / ActiveDirectory / AzureAD / MicrosoftAccount
                }
            })
    } catch {
        # Known quirk: Get-LocalGroupMember throws if any member SID cannot be resolved. Fall back to net localgroup.
        Write-Warning "Get-LocalGroupMember failed ($($_.Exception.Message)); falling back to 'net localgroup'."
        $raw = @(& net localgroup $Group 2>$null)
        $dash = ($raw | Select-String -Pattern '^-{3,}$' | Select-Object -First 1)
        if (-not $dash) { return $null }
        $names = $raw[$dash.LineNumber..($raw.Count - 1)] |
            Where-Object { $_ -and ($_ -notmatch 'command completed successfully') }
        return @($names | ForEach-Object {
                [pscustomobject]@{ Name = $_.Trim(); Sid = ''; ObjectClass = 'Unknown'; Source = 'Unknown' }
            })
    }
}

function Get-MemberClass {
    param($Member)
    if ($Member.Sid -match '-500$') { return 'built-in Administrator (expected)' }
    if ($Member.Sid -match '-5(12|18|19)$') { return 'privileged group (expected)' }   # Domain/Enterprise Admins, SYSTEM
    if ($Member.ObjectClass -eq 'Group') { return 'group (review its members)' }
    if ($Member.ObjectClass -eq 'Unknown') { return 'review' }
    return 'USER ACCOUNT - remove'
}

$group = Get-AdminGroupName
$members = Get-AdminMembers -Group $group

Write-Host ''
Write-Host "NISS anti-tamper audit - $env:COMPUTERNAME" -ForegroundColor Cyan
Write-Host ('=' * 50)

$me = [Security.Principal.WindowsIdentity]::GetCurrent()
$elevated = ([Security.Principal.WindowsPrincipal]$me).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host ("Current user : {0}" -f $me.Name)
# Note: membership is resolved against the group list below. IsInRole() reflects whether THIS process is elevated
# (UAC filters the admin token), which is not the same question as "is this account allowed to kill the agent".
$mySid = $me.User.Value
$iAmAdminMember = $members -and (@($members | Where-Object { $_.Sid -eq $mySid }).Count -gt 0)
Write-Host ("  listed as a local admin : {0}" -f $(if ($iAmAdminMember) { 'YES - this account can stop/remove the agent' } elseif ($members) { 'not directly (also check the groups below)' } else { 'unknown (could not read group)' })) `
    -ForegroundColor $(if ($iAmAdminMember) { 'Yellow' } else { 'Green' })
Write-Host ("  (this PowerShell process elevated : {0})" -f $(if ($elevated) { 'yes' } else { 'no' })) -ForegroundColor Gray

foreach ($s in @('NISSMonitorService', 'Watchdog')) {
    $svc = Get-Service $s -ErrorAction SilentlyContinue
    Write-Host ("Service {0,-20}: {1}" -f $s, $(if ($svc) { $svc.Status } else { 'NOT INSTALLED' }))
}

if ($null -eq $members) {
    Write-Host ''
    Write-Warning "Could not read the '$group' group. Run elevated, or check locally with: net localgroup `"$group`""
    exit 1
}

Write-Host ''
Write-Host "Members of local group '$group' (anyone here can stop/remove the agent):" -ForegroundColor Cyan
$rows = $members | ForEach-Object { [pscustomobject]@{ Account = $_.Name; Type = $_.ObjectClass; Source = $_.Source; Assessment = (Get-MemberClass $_) } }
$rows | Format-Table -AutoSize | Out-String | Write-Host

$toRemove = $members | Where-Object { (Get-MemberClass $_) -eq 'USER ACCOUNT - remove' }
if ($toRemove) {
    Write-Host "User accounts that should probably NOT be local admins - remove deliberately:" -ForegroundColor Yellow
    foreach ($m in $toRemove) {
        Write-Host ("    Remove-LocalGroupMember -Group '{0}' -Member '{1}'" -f $group, $m.Name)
    }
    Write-Host ''
    Write-Host "A removed user keeps working normally - they just can no longer stop or uninstall the agent." -ForegroundColor Gray
    Write-Host "For staff who genuinely need admin, see docs\least-privilege-rollout.md (separate admin account)." -ForegroundColor Gray
    exit 2
}

Write-Host "No ordinary user accounts found in the Administrators group on this PC." -ForegroundColor Green
exit 0
