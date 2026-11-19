# Approval rules

Rollcall supports mandatory four-eyes approval before high-impact changes run. Admins configure approval rules under **Admin › Settings**.

## Actions you can gate behind approval

- **Offboarding** (enabled by default)
- **Conditional Access policy changes** (always required; cannot be disabled)
- **Bulk changes** exceeding your configured user threshold
- **Onboarding** (optional)
- **License or group modifications** (optional)

## How approvals work

1. A technician builds and previews a change. Rollcall freezes the exact planned diff and computes a SHA-256 `plan_hash`.
2. When submitted, the run enters **Awaiting approval** and appears in **Approvals**.
3. An **Approver** or **Admin** reviews the exact frozen diff and clicks **Approve & run** or **Reject request** (rejection requires a non-empty reason).
4. **Self-approval is blocked by default:** the user who requested a run cannot approve their own request unless an Admin explicitly enables **Allow self-approval** for small-team environments.
5. **Drift detection:** before applying an approved plan, the background worker re-reads the live directory. If any field or membership changed outside Rollcall while waiting for approval, the run pauses in **Needs attention** with a clear diff instead of overwriting unexpected changes.
