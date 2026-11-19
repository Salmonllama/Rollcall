# Previews, drift detection, and retries

Rollcall never changes your directory on a blind click. Every write goes through **Plan → Preview → Confirm → Run**.

## Reading a Change Preview

Before any change runs, Rollcall displays a **Change Preview** showing:

- A one-line summary (`5 changes across Entra ID and Exchange Online`).
- Every step grouped by target system, with the exact **before** and **after** values.
- Any **No-op** items (already in the desired state; skipped automatically).
- **Warnings** and **blocking errors** (for example a UPN collision or an out-of-scope AD OU).
- Whether **Approval** is required before execution.

## Drift detection (`Needs attention`)

When you confirm a plan, Rollcall freezes it with a SHA-256 hash. If an outside admin edits the same attribute or membership while a run waits for approval or its scheduled time, Rollcall detects the drift before writing and pauses the run in **Needs attention**. Review the diff and either re-plan or cancel.

## Retrying failed steps

If a step fails partway through (for example due to a temporary Microsoft Graph error):

1. Open the run page (**Runs › <run>**).
2. Read the plain-language failure reason and suggested next step on the failed step.
3. Click **Retry failed steps**. Rollcall re-runs *only* the failed and remaining steps—already-succeeded steps are never duplicated.
