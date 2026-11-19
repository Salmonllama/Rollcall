# Step 10: Go-live checklist

**What you're doing:** Verifying every security, operational, and governance control before opening Rollcall to your full IT team.

Work through every box below before announcing Rollcall to technicians and approvers:

- [ ] **HTTPS certificate is trusted** — Opening your Rollcall URL in a standard browser shows a valid padlock with no certificate warnings.
- [ ] **Setup wizard is locked** — Navigating to `/setup` returns `404` or redirects to the portal; `ROLLCALL_SETUP_TOKEN` is no longer accepted.
- [ ] **Minimum team roles assigned** — At least two users hold the **Admin** role in **Admin › Team** (or you have tested `install.sh admin-reset`), and technicians have only the **Technician** role.
- [ ] **Approval rules reviewed** — Open **Admin › Settings** and verify which sensitive actions (offboarding, Conditional Access changes, bulk changes) require an **Approver** and that self-approval is disabled.
- [ ] **Conditional Access break-glass accounts registered** — If you enable Conditional Access management, every emergency access (break-glass) account is listed in **Admin › Conditional Access** so Rollcall hard-blocks removing them from exclusions.
- [ ] **Role templates validated** — Every production role template passes live validation against Entra ID (and Active Directory OUs if hybrid).
- [ ] **Hybrid AD agent verified (hybrid only)** — **Admin › AD Agents** shows **Online**, the gMSA only has delegated rights on approved OUs, and an out-of-scope OU edit is blocked.
- [ ] **Health page is all green** — **Admin › Health** reports **Healthy** across PostgreSQL, Background worker, Microsoft Graph, Exchange Online, Permissions, App certificate, AD agent (if enabled), and Last backup.
- [ ] **Off-host backup verified** — A daily backup schedule is active, archives are copied off the Linux host, and you know how to run `install.sh restore <archive>`.
- [ ] **Audit log verified** — **Admin › Audit log › Verify hash chain** reports intact, and **Export CSV** downloads cleanly.
- [ ] **Certificate expiry monitoring noted** — Admins know Rollcall shows a banner 30 days before the Entra app certificate expires, and how to run the guided zero-downtime rotation in **Admin › Connections**.

## Check it worked

Once every box above is checked, share your Rollcall URL and the [Technician guides](../technician/dashboard.md) with your helpdesk team.
