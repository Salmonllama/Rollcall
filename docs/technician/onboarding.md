# Onboarding a new user

Open **Onboarding** in the sidebar to create and provision a new user from an approved role template.

## Steps

1. Pick a **Role template** (cloud or hybrid).
2. Enter the new user's name, start date (optional for scheduled activation), manager, and any template overrides.
3. Click **Preview changes**. Rollcall checks live Entra ID (and Active Directory if hybrid) for UPN, mail, and `sAMAccountName` collisions.
   - If a collision exists, Rollcall **blocks** the plan with a clear message and lets you choose a different username—Rollcall never silently invents `j.smith2`.
4. Confirm the plan (or submit for approval if required).

## Scheduled onboarding

If you pick a future **Start date & time**, Rollcall runs in two phases:

- **Phase 1 (immediate):** Creates the account with sign-in **blocked** (or disabled in AD), assigns groups and licenses, and configures the mailbox.
- **Phase 2 (scheduled start date):** Automatically unblocks sign-in and issues the Temporary Access Pass on the user's start date.

## Welcome Pack PDF

Once onboarding succeeds, click **Download Welcome Pack PDF** to save a clean handoff sheet for the new user's manager. The PDF never includes the temporary password or TAP—deliver the one-time credential separately.
