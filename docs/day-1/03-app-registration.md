# Step 3: Create the Entra app registration

**What you're doing:** Creating the single-tenant Microsoft Entra ID app registration, uploading Rollcall's RSA 3072 public certificate, and granting the minimum Graph and Exchange permissions.

## Option A: Run the setup wizard's PowerShell script (recommended)

1. Open your Rollcall portal URL (for example `https://rollcall.example.com`) and enter the setup token printed at the end of Step 2 (also stored in `/opt/rollcall/.env` as `ROLLCALL_SETUP_TOKEN`).
2. In the **App registration** step, click **Generate RSA 3072 certificate**. Rollcall generates a 3072-bit RSA keypair, encrypts the private key in PostgreSQL with AES-256-GCM, and offers the public `.cer` file plus a ready-to-run PowerShell script.
3. Click **Download public certificate (.cer)** and copy the generated PowerShell script.
4. On a workstation with `Microsoft.Graph` installed, sign in as a Global Administrator or Privileged Role Administrator and run the script:

```powershell
Install-Module Microsoft.Graph.Applications -Scope CurrentUser
Connect-MgGraph -Scopes "Application.ReadWrite.All","AppRoleAssignment.ReadWrite.All","RoleManagement.ReadWrite.Directory"
./register-rollcall-app.ps1 -CertPath .\rollcall-app.cer
```

## Option B: Create the app registration manually in Entra

1. In the **Microsoft Entra admin center**, go to **Identity › Applications › App registrations › New registration**.
2. Name the app `Rollcall`, select **Accounts in this organizational directory only (Single tenant)**, and add a **Web** redirect URI:
   ```text
   https://<your-rollcall-hostname>/api/auth/oidc/callback
   ```
3. Under **Certificates & secrets › Certificates**, upload the `.cer` public certificate downloaded from Rollcall. Do **not** create a client secret; Rollcall authenticates exclusively with its RSA 3072 certificate.
4. Under **API permissions**, add the Microsoft Graph application permissions listed in the [Permissions reference](../reference/permissions.md), plus `Office 365 Exchange Online` › `Exchange.ManageAsApp` if you manage mailboxes or distribution lists, then click **Grant admin consent**.

## Expected result

The script prints your **Tenant ID** and **Application (client) ID**, and every required permission shows **Granted** in Entra ID.

## Check it worked

1. Paste the **Tenant ID**, **Application (client) ID**, and primary domain into Rollcall's setup wizard and click **Validate connection**.
2. Confirm every enabled feature's permissions show **Granted** (green) and the organization name matches your tenant.

Next: [Step 4: Assign directory roles](04-directory-roles.md)
