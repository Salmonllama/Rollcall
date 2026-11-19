# MFA, Temporary Access Passes, and sessions

Open a user's **MFA & sign-in** tab to help users who lost a device, need first-time MFA setup, or suspect a compromised session.

## Issuing a Temporary Access Pass (TAP)

1. Choose **Issue Temporary Access Pass**, select the lifetime (for example 60 minutes) and whether it is single-use.
2. Preview and confirm.
3. On the completed run page, click **View temporary credential** once. The TAP is shown in monospace with a copy button and **cannot be viewed a second time** (`410 Gone`). It is never stored in PostgreSQL or the audit log.

## Resetting authentication methods & sessions

- **Remove authentication method:** Removes a lost phone, FIDO2 key, or Authenticator app registration. Rollcall warns before removing the user's *last* remaining MFA method.
- **Revoke sessions:** Calls Microsoft Graph `revokeSignInSessions` to invalidate active refresh tokens across Microsoft 365 apps and browsers.
- **Reset password:** Generates a strong temporary password (cloud users via Entra ID, hybrid users via the AD agent) with force-change-at-next-sign-in, displayed once via the same ephemeral single-view flow.
