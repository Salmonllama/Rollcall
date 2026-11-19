# Step 6: Hybrid only: install the AD agent

**What you're doing:** Installing the outbound-only Windows AD agent (`Rollcall.Agent`) on a domain-joined server under a least-privilege gMSA, scoped to an explicit allowlist of Active Directory OUs.

Skip this step if all of your users and groups are cloud-only.

## Prerequisites

- A domain-joined Windows Server 2019 or 2022 host with .NET 10 Runtime installed.
- Outbound HTTPS (TCP 443) from that host to your Rollcall portal URL. No inbound firewall ports are opened on the Windows host.
- A KDS root key present in Active Directory (standard for gMSA creation).

## 1. Create the gMSA and delegate OU permissions

On a domain-joined admin workstation or the target Windows server, run `New-RollcallGmsa.ps1` from `src/Rollcall.Agent.Installer/` (passing the exact OUs Rollcall is allowed to manage):

```powershell
.\New-RollcallGmsa.ps1 `
  -ServiceAccountName "gmsa-Rollcall" `
  -AllowedHostSamAccountName "ADAGENT01$" `
  -AllowedOUs @(
    "OU=Staff,OU=Users,DC=contoso,DC=local",
    "OU=Disabled Users,DC=contoso,DC=local",
    "OU=Groups,DC=contoso,DC=local"
  )
```

> **Warning:** Never include Domain Admins, Enterprise Admins, Builtin, or Tier-0 admin OUs in `-AllowedOUs`. The AD agent enforces the OU allowlist in code, and AD delegation provides defense in depth.

## 2. Generate a single-use enrollment token in Rollcall

1. Sign in to Rollcall as an **Admin** and open **Admin › AD Agents**.
2. Enter a friendly host name (for example `ADAGENT01`) and paste the exact allowed OU distinguished names.
3. Click **Generate enrollment token**. The token expires in 15 minutes and can only be used once.

## 3. Install and enroll the Windows service

On the Windows server, run `install-agent.ps1` in an elevated PowerShell window:

```powershell
.\install-agent.ps1 `
  -RollcallUrl "https://rollcall.example.com" `
  -EnrollmentToken "<paste-single-use-token>" `
  -ServiceAccount "CONTOSO\gmsa-Rollcall$" `
  -AllowedOUs @(
    "OU=Staff,OU=Users,DC=contoso,DC=local",
    "OU=Disabled Users,DC=contoso,DC=local",
    "OU=Groups,DC=contoso,DC=local"
  ) `
  -LdapHost "dc01.contoso.local" `
  -UseLdaps $true
```

## Expected result

The `RollcallAgent` Windows service starts under the gMSA, generates an ECDSA P-256 keypair protected by Windows DPAPI (`LocalMachine`), exchanges the enrollment token for its agent ID, and begins polling `https://rollcall.example.com/api/agent/jobs/poll`.

## Check it worked

1. Refresh **Admin › AD Agents** in Rollcall. Confirm the agent shows **Online** with a heartbeat timestamp within the last 30 seconds and lists your allowed OUs.
2. Open **Admin › Health** and confirm **AD Agent** reports **Healthy**.

Next: [Step 7: Build the first role template](07-first-role-template.md)
