# Managing groups and distribution lists

Open **Groups** in the sidebar—or the **Groups** tab on any user—to add or remove members across:

- **Microsoft Entra security groups** and **Microsoft 365 / Teams groups** (executed via Microsoft Graph `$batch` up to 20 operations per call).
- **Exchange Online distribution lists** and **mail-enabled security groups** (routed automatically through Rollcall's pooled Exchange Online runner).
- **AD-synced on-premises groups** (routed automatically through the Windows AD agent when within an allowed OU).

Dynamic groups are displayed as read-only so you can see membership without attempting unsupported manual edits.
