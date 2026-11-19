# Team and portal roles

Portal access is **invite-only** and authenticated solely via single-tenant Microsoft Entra ID OIDC. Manage members under **Admin › Team**.

## Roles

| Role | Capabilities |
| --- | --- |
| **Admin** | Full access: tenant connection, certificate rotation, team roles, role templates, approval settings, Conditional Access management, AD agents, health, and audit log |
| **Approver** | Can review, approve, or reject pending workflow runs in **Approvals** (cannot approve own requests unless self-approval is explicitly enabled) |
| **Technician** | Can browse Users/Groups/Licenses, edit user attributes, manage groups/licenses/MFA/sessions, and run Onboarding and Offboarding |
| **Auditor** | Read-only access to Directory views, Workflow runs, and the Audit log (including CSV export and hash chain verification) |

Users can hold multiple roles (for example `Technician` + `Approver`). Roles are checked on the server on every API request—never only in the UI.

## Preventing admin lockout

- Rollcall blocks demoting or disabling the last active **Admin** in the UI and API.
- If all Admin accounts lose Entra access, restore Admin access from the Linux host:

```bash
sudo bash /opt/rollcall/install.sh admin-reset alex.chen@contoso.com
```
