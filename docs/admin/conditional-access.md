# Conditional Access management

Conditional Access (CA) management is **Admin-only** and lives under **Conditional Access** in the sidebar. Rollcall is deliberately scoped to safe user/group inclusion and exclusion adjustments—it never edits grant controls, cloud app scopes, session controls, or policy state.

## Safety rails

1. **Strict `conditions.users` PATCH only:** Rollcall only sends `conditions.users` (`includeUsers`, `excludeUsers`, `includeGroups`, `excludeGroups`) in Microsoft Graph `PATCH` calls. All other policy properties remain untouched.
2. **Break-glass account protection:** Register all emergency access (break-glass) accounts in **Admin › Conditional Access**. Rollcall hard-blocks any plan that would remove a break-glass account from a policy's exclusions.
3. **Policy-wide lockout guard:** Rollcall warns or blocks changes that would drop all exclusions on an enabled policy without a break-glass exclusion remaining.
4. **Mandatory approval + typed confirmation:** Every CA policy change requires typing the exact policy display name and receiving approval from a second **Approver** or **Admin** before execution.

## Timed temporary exclusions

When a user needs a temporary exclusion (for example during travel or hardware replacement):

1. Open the target CA policy and choose **Add timed exclusion**.
2. Select the user, choose a duration (such as 4 hours, 24 hours, or 7 days), and enter a ticket/reason.
3. Preview, confirm, and approve the run.
4. Rollcall adds the user to `excludeUsers` and schedules an automatic reversal job in `scheduled_reversal`. When the timer expires, the background worker automatically removes the user from `excludeUsers` and logs the reversal in the audit log.
