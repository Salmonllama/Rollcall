# Step 7: Build the first role template

**What you're doing:** Creating a versioned role template so onboarding new users is repeatable, validated against live directory objects, and typo-free.

## Steps

1. Sign in to Rollcall as an **Admin** and open **Admin › Role templates**.
2. Click **Create template** and enter a descriptive name (for example `Standard Staff — Cloud`).
3. Configure:
   - **Account mode:** `Cloud` (or `Hybrid` if you completed Step 6 and want accounts created in on-premises Active Directory first).
   - **Naming patterns:** UPN pattern (`{first}.{last}@{domain}`) and display name (`{first} {last}`).
   - **Defaults:** Department, Job title, Company, Office, Usage location (for example `US`), and default Manager (optional).
   - **Groups & distribution lists:** Pick live Entra groups, Teams, or distribution lists from your tenant.
   - **Licenses:** Select one or more SKUs and optionally toggle individual service plans.
   - **Initial credential:** Choose **Temporary Access Pass (TAP)** (recommended) or temporary password, and set the lifetime.
   - **Mailbox defaults:** Time zone, language, and shared mailbox assignments (if applicable).
   - **Welcome Pack PDF:** Enable **Generate Welcome Pack PDF** and optionally enable **Hide Rollcall branding** if you prefer a plain/organization-only header.
4. Click **Validate & save template**. Rollcall checks every referenced group, license SKU, and OU against your live directory before saving.

## Expected result

The template appears in **Admin › Role templates** at `Version 1` with status **Active**.

## Check it worked

Open **Onboarding** in the sidebar and confirm your new role template appears in the **Role template** picker.

Next: [Step 8: Run a test onboarding and offboarding](08-test-lifecycle.md)
