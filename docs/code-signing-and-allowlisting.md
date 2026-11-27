# Code-signing and allow-listing (layer 4 anti-tamper)

This layer stops a technical employee from **replacing or swapping out** the agent binaries — e.g. overwriting
`Windows.RunService.exe` with a do-nothing stub, or running a recompiled agent that reports fake activity — and makes
the official installer trusted by Windows/SmartScreen instead of "unknown publisher". It has two parts: **sign** the
binaries with your certificate, then **allow-list** only your signature so nothing else can run in their place.

It is complementary to the other layers: hardening (layer 2) stops the service being stopped; signing + allow-listing
stops the files being substituted.

## 1. Get a code-signing certificate

Use your organisation's existing code-signing certificate, or buy one (OV/EV code-signing) from a public CA. For a
purely internal LAN deployment you can instead issue one from your own enterprise CA (ADCS) and have it chain to a
root your PCs already trust — that is enough for AppLocker/WDAC publisher rules, though not for public SmartScreen
reputation. Keep the private key in your HSM / certificate store; never put it in the repo.

## 2. Sign the binaries

Sign the staging folder **before** it is packaged into `NISSMonitorSetup.exe` (so the embedded payload is signed too),
then let `Build-Agent.ps1` package it:

```powershell
# from a cert in your certificate store (by thumbprint)
.\deployment\scripts\Sign-Agent.ps1 -PayloadDir .\artifacts\agent\payload -Thumbprint <sha1-thumbprint>

# or from a PFX
.\deployment\scripts\Sign-Agent.ps1 -PayloadDir .\artifacts\agent\payload -PfxPath .\niss-codesign.pfx
```

Then sign the finished `NISSMonitorSetup.exe` as well (same script with `-PayloadDir` pointing at `artifacts\agent`).
Signatures are timestamped, so they stay valid after the certificate expires.

## 3. Allow-list only your signature

### AppLocker (simplest; Windows Enterprise/Education)

Allow only binaries published under your certificate's subject, and block everything else from the usual user-writable
locations. Create a **publisher** rule from a signed file and deploy it by GPO or Intune:

```powershell
# On a reference PC with a signed NISS binary present:
$signed = Get-AppLockerFileInformation 'C:\Program Files\NISS\Monitor\Windows.RunService.exe'
# Build a publisher rule (Publisher = your cert subject) for EXE and DLL collections, set to Enforce, and export:
$policy = New-AppLockerPolicy -FileInformation $signed -RuleType Publisher -User Everyone -Optimize
$policy | Set-AppLockerPolicy -XmlPolicy .\niss-applocker.xml
```

Deploy `niss-applocker.xml` through **GPO** (Computer Configuration → Windows Settings → Security Settings →
Application Control Policies → AppLocker) or **Intune** (Endpoint security → Attack surface reduction → App control).
Enable the **Application Identity** service. Enforce EXE and DLL rules. Pair the publisher *allow* rules with the
default rules so ordinary software keeps working; the point is that an unsigned or differently-signed replacement of a
NISS binary will be blocked.

> Test in **Audit** mode first (it logs would-be blocks to the AppLocker event log without enforcing), confirm nothing
> legitimate is caught, then switch to **Enforce**.

### WDAC (Windows Defender Application Control; stronger, more involved)

WDAC enforces at the kernel and is harder for an admin to bypass than AppLocker. Build a policy from your signer with
`New-CMMappedSigningRule` / the WDAC Wizard, keyed on your certificate, and deploy signed policies via Intune/MEMCM.
This is a larger project — treat it as a hardening step for high-assurance fleets once AppLocker is proven.

## How it fits with the other layers

| Attack | Stopped by |
|---|---|
| Stop/disable/delete the service (GUI/one-click) | Layer 2 service-ACL hardening |
| End the process in Task Manager | Mutual watchdog (restart in ~5–13 s) |
| Replace a NISS binary with a stub / run a fake rebuild | **Layer 4 signing + allow-listing** |
| Deliberate CLI removal by an admin (`sc sdset`, disable+reboot) | Layer 3 tamper detection (alerts you) + policy |
| Employee is not an admin at all | Layer 1 least privilege (can't do any of the above) |

See also: [security.md](security.md) · [least-privilege-rollout.md](least-privilege-rollout.md).
