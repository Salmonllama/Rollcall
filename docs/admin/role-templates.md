# Role templates

Role templates standardize onboarding so technicians never type group names, license SKUs, or OU paths by hand. Manage them under **Admin › Role templates**.

## Live validation & versioning

- Every time an Admin saves a role template, Rollcall validates referenced Entra groups, distribution lists, license SKUs, service plans, and Active Directory OUs against the live directory.
- Invalid or removed references block saving with the exact missing item name.
- Editing an existing template creates a new version (`v2`, `v3`, …). Historical onboarding runs permanently reference the template version used when they ran.
- Archiving a template hides it from the onboarding picker while preserving audit history.

## Welcome Pack PDF branding

Each template controls whether Rollcall generates a downloadable **Welcome Pack PDF** after onboarding:

- **Default:** includes the Rollcall wordmark, your organization's custom header/letterhead text, account details, and setup steps.
- **Hide Rollcall branding:** omits the Rollcall wordmark and footer completely, rendering only your organization's name and custom header text.

> **Info:** Welcome Pack PDFs never include the Temporary Access Pass or temporary password so they can be emailed safely to managers or HR. Deliver the one-time credential separately.
