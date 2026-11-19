# Step 5: Sign in as the first admin

**What you're doing:** Completing the setup wizard with your Microsoft work account, locking the wizard, and inviting your initial team.

## Steps

1. On the final screen of the setup wizard, click **Sign in with Microsoft** using the work account that should be Rollcall's first **Admin**.
2. Approve any standard OIDC sign-in prompts for your tenant (`openid`, `profile`, `email`).
3. Rollcall records your Entra Object ID and UPN in `portal_user` with the `Admin` role, marks setup complete, and permanently disables `/setup`.
4. Open **Admin › Team** to invite additional admins, approvers, technicians, or read-only auditors. Portal access is invite-only: Entra users who have not been invited cannot sign in.

## Expected result

You land on the Rollcall **Dashboard** with your name and the **Admin** role shown in the top bar. Navigating to `/setup` now redirects to `/` or returns `404`.

## Check it worked

1. Open **Admin › Connections** and verify the tenant status is **Connected** and the app certificate expiry date is approximately 1 year out.
2. Open **Admin › Audit log** and verify a `setup.completed` entry and your sign-in entry are recorded, then click **Verify hash chain** to confirm the audit chain is intact.
3. Open **Users** and search for a known user in your tenant to confirm Microsoft Graph reads succeed.

> **Info:** If you ever lose access to all Admin accounts, restore Admin access from the Linux host with `sudo bash /opt/rollcall/install.sh admin-reset <your-upn>`.

Next: [Step 6: Hybrid only: install the AD agent](06-ad-agent.md) (or skip to [Step 7: Build the first role template](07-first-role-template.md) if your tenant is cloud-only)
