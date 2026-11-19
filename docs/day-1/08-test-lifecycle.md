# Step 8: Run a test onboarding and offboarding

**What you're doing:** Running a complete end-to-end onboarding and offboarding against a fictional test account to verify preview diffs, approvals, Exchange mailbox conversion, and audit logging before real staff use Rollcall.

## 1. Onboard a test user

1. Open **Onboarding**, choose the template you created in Step 7, and enter a fictional test user (for example `Rollcall Test`, `rollcall.test@<your-domain>`).
2. Click **Preview changes**. Review every planned step grouped by system (Entra ID, Exchange Online, and Active Directory if hybrid). Confirm there are no UPN or alias collisions.
3. Click **Run onboarding** (or **Submit for approval** if onboarding requires approval under **Admin › Settings**).
4. Watch the live step-by-step progress on the run page until every step shows **Succeeded**.
5. Click **View temporary credential** once to confirm the TAP/password renders once and returns **Already viewed** on any second attempt.
6. Click **Download Welcome Pack PDF** and verify the PDF opens cleanly with the new user's details and setup instructions.

## 2. Offboard the test user

1. Open **Offboarding** and select the test user you just created.
2. Choose **Block sign-in**, **Revoke sessions**, **Convert mailbox to shared**, **Remove direct licenses**, and **Hide from GAL**.
3. Click **Preview changes** and verify the plan explicitly orders **Convert mailbox to shared** *before* **Remove licenses**.
4. Type the test user's exact UPN in the confirmation dialog and click **Run offboarding** (and approve as a second user if approval is required).

## Expected result

Both runs finish with status **Succeeded**. The test user is blocked from signing in, sessions are revoked, the mailbox is converted to shared, and licenses are reclaimed.

## Check it worked

1. Open **Admin › Audit log**, filter by your test user's UPN, and confirm every step and credential view is recorded with timestamps and actor identity.
2. Click **Verify hash chain** to confirm the audit log integrity check passes.
3. Optional: Click **Restore user** on the completed offboarding run to confirm Rollcall restores the account cleanly, then remove the test account when finished.

Next: [Step 9: Set up and test backups](09-backups.md)
