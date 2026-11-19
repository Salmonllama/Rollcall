# AD agent management (hybrid environments)

In hybrid Active Directory + Microsoft Entra ID environments, Rollcall routes on-premises user and group mutations to the outbound-only Windows service `Rollcall.Agent`. Manage enrolled agents under **Admin › AD Agents**.

## Security architecture

- **Outbound HTTPS only:** The agent polls Rollcall over HTTPS (TCP 443). Rollcall never connects inbound to your Windows servers or domain controllers.
- **gMSA identity:** The service runs under a Group Managed Service Account (`gmsa-Rollcall$`) with no stored password.
- **Asymmetric JWT authentication:** On enrollment, the agent generates an ECDSA P-256 keypair stored via Windows DPAPI (`LocalMachine`) and signs short-lived 5-minute JWTs with unique `jti` replay protection.
- **Dual-layer OU scope guard:** Both Rollcall API and the local AD agent enforce an explicit allowlist of Distinguished Names (`AllowedOUs`). Attempts to create, modify, move, or disable accounts outside those OUs are rejected before any LDAP write occurs.

## Offline handling and revocation

- If the AD agent stops heartbeating for more than 2 minutes, **Admin › AD Agents** and **Admin › Health** mark it **Offline**.
- Hybrid workflow runs wait cleanly in **Waiting for AD agent** and automatically resume once the agent reconnects.
- To decommission or immediately block a compromised host, click **Revoke agent** in **Admin › AD Agents**. Subsequent poll and heartbeat requests return `401 Unauthorized` immediately.
