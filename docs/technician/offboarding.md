# Offboarding and restoring a user

Open **Offboarding** in the sidebar to safely revoke access, preserve mailbox data, hand over OneDrive files, and reclaim licenses.

## Enforced safety order

Rollcall enforces a strict execution order so mailbox data is never lost:

1. **Block sign-in** (and disable AD account + move to disabled OU for hybrid users)
2. **Revoke active sessions** and clear registered MFA methods (optional)
3. **Convert mailbox to shared**, hide from GAL, configure out-of-office reply, and set forwarding (optional)
4. **Grant OneDrive handover access** to the departing user's manager with an automatic timed reversal (default 30 days)
5. **Remove group memberships** and distribution list memberships (preserving dynamic groups)
6. **Remove direct licenses** — **always scheduled strictly after mailbox conversion succeeds**

If the mailbox conversion step fails, Rollcall **halts** and does **not** remove the user's license so the mailbox is never soft-deleted by mistake.

## Confirmation and approval

Offboarding always requires typing the departing user's exact UPN in the confirmation dialog, and requires an **Approver** or **Admin** sign-off by default.

## Restoring an offboarded user

If an offboarding needs to be reversed (for example a contractor returning or an offboarding run on the wrong date), open the completed offboarding run and click **Restore user**. Rollcall builds a preview plan that unblocks sign-in, converts the mailbox back to a regular user mailbox (when a license is re-assigned), restores removed groups and licenses recorded in the run's undo metadata, and unhides the user from the GAL.
