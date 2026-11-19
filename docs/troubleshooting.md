# Troubleshooting

Common issues and exact commands to resolve them.

## Rollcall isn't reachable over HTTPS

1. Check service health on the Linux host:
   ```bash
   sudo bash /opt/rollcall/install.sh status
   ```
2. Inspect Caddy proxy logs:
   ```bash
   sudo bash /opt/rollcall/install.sh logs proxy
   ```
3. If using `--tls letsencrypt`, verify public DNS points to this server and inbound TCP ports 80 and 443 are open.

## `/readyz` reports `worker: stale`

1. Inspect worker logs:
   ```bash
   sudo bash /opt/rollcall/install.sh logs worker
   ```
2. Check database connectivity and disk space (`df -h`). The worker writes a heartbeat to `worker_heartbeat` every 15 seconds.

## Microsoft Graph returns `403 Forbidden` or missing permissions

1. Open **Admin › Connections** or **Admin › Health** to see the exact missing permission or directory role.
2. Compare against the [Permissions reference](reference/permissions.md), grant admin consent in Microsoft Entra ID, and click **Re-check connection**.
3. Wait up to 2 minutes for Entra ID to reflect newly granted app roles in fresh tokens.

## Exchange Online step fails with authentication or module errors

1. Verify `Exchange.ManageAsApp` is granted and **Exchange Administrator** is assigned to the Rollcall service principal.
2. Confirm the active certificate in **Admin › Connections** is RSA 3072 (Exchange Online rejects ECDSA certificates; see [ADR 0002](adr/0002-exchange-online-and-certificates.md)).
3. Confirm the worker container can reach `www.powershellgallery.com` on first run to fetch the pinned `ExchangeOnlineManagement` 3.10.1 module (verified by SHA-256 tree hash).

## Run is paused in `Needs attention` (drift detected)

Someone modified the user or group outside Rollcall after the plan was previewed. Open the run page to compare the planned vs. live values, then either cancel and re-plan with current values or resolve the outside change first.

## AD agent shows `Offline` or rejects a hybrid step

1. On the Windows server hosting `Rollcall.Agent`, check the Windows Service and Application Event Log:
   ```powershell
   Get-Service RollcallAgent
   Get-EventLog -LogName Application -Source RollcallAgent -Newest 30
   ```
2. Confirm outbound HTTPS (TCP 443) to your Rollcall portal URL succeeds.
3. If the error mentions `OU outside allowed scope`, verify the target user or group's Distinguished Name falls under an OU listed in both **Admin › AD Agents** and the agent's local `appsettings.json`.

## Locked out of all Admin accounts

Run the host-shell recovery command on the Linux server:

```bash
sudo bash /opt/rollcall/install.sh admin-reset your.admin@contoso.com
```

Then sign out and sign back in to the portal.

## Audit hash chain verification fails

If **Admin › Audit log › Verify hash chain** reports a broken link at row ID `N`, treat it as a potential database tampering or corruption incident: export the audit log, inspect PostgreSQL access logs, and compare against your most recent off-host backup archive.
