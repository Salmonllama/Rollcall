# Guided certificate rotation

Rollcall authenticates to Microsoft Graph and Exchange Online using a 3072-bit RSA certificate valid for 1 year. Rollcall warns Admins with a banner starting 30 days before expiry.

## Zero-downtime rotation steps

1. Open **Admin › Connections** and click **Start certificate rotation**.
2. Rollcall generates a new **Pending** RSA 3072 certificate (keeping the current **Active** certificate running) and lets you download the new `.cer` public certificate.
3. Upload the new `.cer` file to your `Rollcall` app registration in Microsoft Entra ID (**Certificates & secrets › Certificates › Upload certificate**). Keep the old certificate uploaded while rotating.
4. Return to **Admin › Connections** and click **Verify & activate new certificate**. Rollcall acquires a live test token using the new certificate before switching it to **Active**.
5. Once active, click **Retire previous certificate** in Rollcall, then remove the old certificate from Microsoft Entra ID.

> **Info:** Private keys are generated in memory, encrypted with AES-256-GCM using `ROLLCALL_MASTER_KEY`, and never written to disk.
