# Permissions reference

This file is generated from Rollcall's permission manifest. Every workflow step declares the Microsoft Graph application permissions, Exchange Online permissions, and Entra ID directory roles it requires. Turning a feature off in **Settings** drops its permissions from this manifest and from the setup script.

## Application permissions and directory roles

| Feature | API | Application permission | Directory role | Used for |
| --- | --- | --- | --- | --- |
| `conditionalAccess` | Microsoft Graph | `Policy.Read.All` | None | Read Conditional Access policies |
| `conditionalAccess` | Microsoft Graph | `Policy.ReadWrite.ConditionalAccess` | None | Edit Conditional Access policy include and exclude targets |
| `core` | Microsoft Graph | `AuditLog.Read.All` | None | Read sign-in audit summaries (optional) |
| `core` | Microsoft Graph | `Directory.Read.All` | None | Read directory roles and service principal assignments |
| `core` | Microsoft Graph | `Group.Read.All` | None | Read security and Microsoft 365 groups and memberships |
| `core` | Microsoft Graph | `GroupMember.ReadWrite.All` | None | Add and remove members of security and Microsoft 365 groups |
| `core` | Microsoft Graph | `Organization.Read.All` | None | Read tenant domain, name, and subscribed SKUs |
| `core` | Microsoft Graph | `User-PasswordProfile.ReadWrite.All` | `User Administrator` | Reset passwords for cloud users |
| `core` | Microsoft Graph | `User.EnableDisableAccount.All` | None | Block or allow sign-in for cloud users |
| `core` | Microsoft Graph | `User.Read.All` | None | Read user profiles, status, and managers |
| `core` | Microsoft Graph | `User.ReadWrite.All` | None | Create and update cloud user accounts and attributes |
| `core` | Microsoft Graph | `User.RevokeSessions.All` | None | Sign users out of all active Microsoft 365 sessions |
| `licenses` | Microsoft Graph | `LicenseAssignment.ReadWrite.All` | None | Assign and remove Microsoft 365 licenses |
| `mailbox` | Office 365 Exchange Online | `Exchange.ManageAsApp` | `Exchange Administrator` | Add and remove members of Exchange Online distribution lists |
| `mfa` | Microsoft Graph | `UserAuthenticationMethod.ReadWrite.All` | `Authentication Administrator` | Manage MFA methods and issue Temporary Access Passes |
| `oneDrive` | Microsoft Graph | `Sites.FullControl.All` | None | Grant and expire manager access to a departing user's OneDrive |

## Checking permissions in Rollcall

Open **Admin › Connections** to compare the permissions granted to your app registration against the features currently enabled. Missing permissions list the exact features affected.
