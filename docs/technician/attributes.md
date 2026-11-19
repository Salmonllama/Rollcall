# Editing user attributes

## Single-user edits

1. Open a user from **Users** and select **Edit attributes**.
2. Update job title, department, office, company, phone numbers, usage location, or manager.
3. Click **Preview changes**, review the before/after table, and click **Run changes**.

## Hybrid source-of-authority routing

If a user is synced from on-premises Active Directory (`Synced from AD`), Rollcall automatically routes AD-mastered attributes (such as display name, title, department, office, and manager) through the **AD agent**, and routes cloud-only attributes (such as usage location) to **Microsoft Entra ID**. You never have to guess which console to use.

## Bulk attribute edits

To update attributes across multiple users at once (for example after a department rename or office move), select multiple users on **Users** and click **Bulk edit attributes**. Bulk changes exceeding your Admin's approval threshold are automatically routed to **Approvals** first.
