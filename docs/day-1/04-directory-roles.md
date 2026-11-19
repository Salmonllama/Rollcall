# Step 4: Assign directory roles

**What you're doing:** Assigning the least-privilege Microsoft Entra directory roles needed for password resets, MFA management, and Exchange Online operations.

## Why directory roles are needed

Microsoft Graph application permissions cover most reads and writes, but Entra ID additionally requires directory roles on the Rollcall service principal for sensitive actions:

| Directory role | Required when |
| --- | --- |
| **Exchange Administrator** | Converting mailboxes to shared, managing distribution lists, hiding users from the GAL, setting out-of-office or forwarding, and configuring mailbox settings via Exchange Online PowerShell |
| **User Administrator** | Resetting passwords and revoking sessions for non-admin users |
| **Authentication Administrator** | Issuing Temporary Access Passes (TAP) and managing user authentication methods |

> **Info:** Rollcall never needs Global Administrator. Assign only the roles for features you enable.

## Assign the roles

1. In the **Microsoft Entra admin center**, go to **Identity › Roles & admins › Roles & admins**.
2. Open **Exchange Administrator**, click **Add assignments**, search for your `Rollcall` enterprise application (service principal), and click **Add**.
3. Repeat for **User Administrator** and **Authentication Administrator** (if you use password resets or MFA/TAP).

Or assign them with PowerShell using the service principal Object ID printed in Step 3:

```powershell
$spId = "<rollcall-service-principal-object-id>"
foreach ($roleName in @("Exchange Administrator", "User Administrator", "Authentication Administrator")) {
    $role = Get-MgDirectoryRole -Filter "displayName eq '$roleName'"
    if (-not $role) {
        $template = Get-MgDirectoryRoleTemplate | Where-Object DisplayName -eq $roleName
        $role = New-MgDirectoryRole -RoleTemplateId $template.Id
    }
    New-MgDirectoryRoleMemberByRef -DirectoryRoleId $role.Id -BodyParameter @{
        "@odata.id" = "https://graph.microsoft.com/v1.0/directoryObjects/$spId"
    }
}
```

## Expected result

The `Rollcall` service principal appears under **Active assignments** for each assigned role.

## Check it worked

Return to Rollcall's setup wizard and click **Re-check connection**. The **Directory roles** section lists each assigned role with a green check mark.

Next: [Step 5: Sign in as the first admin](05-first-admin.md)
