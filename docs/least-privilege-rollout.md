# Stopping employees from killing the agent (least-privilege rollout)

The analysis that prompted this is correct: as shipped, **anyone with local administrator rights on their PC can stop,
disable or uninstall the agent** — Task Manager, `services.msc`, `sc stop`, or the uninstaller. That is by design; the
agent is honest, visible software and deliberately contains no rootkit or anti-admin tricks (see
[security.md](security.md)).

So the question is not "how do we make software an admin can't kill" — no honest software can do that, and the
dishonest kind (a rootkit) would get your agent flagged as malware and destroy the lawful, *disclosed*-monitoring
basis you rely on. The question is **"who has admin, and why."** Fix that and the agent becomes genuinely unkillable
for everyone who shouldn't be able to touch it.

This is layer 1 of four. The others — service-ACL hardening, tamper alerting, and code-signing/WDAC — raise the bar
for the few who keep admin and make tampering loud. This layer is the one that does the most, and it needs no rebuild.

## 1. Find who can kill the agent

Run the read-only audit on a PC (or push it fleet-wide with your RMM/Intune and collect the output or exit code):

```powershell
.\deployment\scripts\Audit-LocalAdmins.ps1
```

It lists the local Administrators group and flags ordinary user accounts that should probably be removed. It changes
nothing. Exit code `2` means "employee-looking admins found" — handy for flagging machines in a fleet sweep.

## 2. Take local admin away from employees

A standard (non-admin) user **cannot** stop/disable/delete the services, kill the SYSTEM processes, or write to
`C:\Program Files\NISS` or `C:\ProgramData\NISS`. They keep working exactly as before — they just lose the ability to
tamper. For each employee account the audit flags:

```powershell
# Standalone / workgroup or Entra-joined PCs (run elevated on the PC):
Remove-LocalGroupMember -Group 'Administrators' -Member '<DOMAIN-or-PC>\<user>'

# Domain fleets: do it centrally instead of per-PC —
#   • Restricted Groups / "Local Users and Groups" Group Policy Preference to SET the exact membership of the local
#     Administrators group (e.g. Domain Admins + a dedicated workstation-admin group only), or
#   • Microsoft Intune "Local admin" / Account Protection policy.
```

Set the membership **explicitly** (Restricted Groups / Intune), don't just remove people once — otherwise an employee
re-adds themselves, or a reimaged PC comes back with the old local admin. Explicit policy re-asserts it every refresh.

> **Before you start:** make sure a *separate*, known admin account (the built-in Administrator with a managed
> password, or a dedicated workstation-admin group) stays in the group, or you will lock yourself out. Never remove the
> last admin.

## 3. The hard case: technical staff who genuinely need admin

Developers and IT often need to install tools or change system settings — so they have admin, and admin can defeat any
software control. Don't try to win that with software. Handle it the way mature IT shops do:

1. **Split the identity.** Their *daily* login is a standard user (the agent is unkillable for it). Local-admin work
   is done with a **separate** admin account (`jdoe` for daily use, `jdoe-adm` for elevation). Most of the day they
   are a standard user, so the window in which they *could* tamper is small and deliberate.
2. **Just-in-time admin.** Grant admin temporarily on request (Intune Endpoint Privilege Management, or Windows LAPS
   for the local Administrator password) instead of standing admin. Elevations are logged.
3. **Govern, don't fight.** For the residual risk, lean on layers 2–4: the hardened service ACL turns a one-click kill
   into a deliberate command-line act, and **tamper alerting** (layer 3) makes that act instant and attributable —
   you get told *which* PC and *which* user, with a final beacon from the agent as it is stopped. Pair it with an
   acceptable-use policy line that says *"disabling the monitoring agent is a policy violation."* For someone who can
   technically defeat any control, the real deterrent is that it is visible, logged, and against the rules — not a
   technical wall.

## The honest ceiling

A determined local administrator can always remove software from their own machine (safe mode, offline disk, resetting
the service ACL). No agent — not this one, not CrowdStrike, not Microsoft Defender — changes that. The goal is
therefore **"can't be killed by people who shouldn't, and instantly caught when someone who can does,"** not
"unkillable." Layers 1–4 together deliver exactly that, within the law and without turning your tool into malware.

See also: [security.md](security.md) · [installation.md](installation.md).
