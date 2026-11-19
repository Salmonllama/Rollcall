# Audit log & integrity verification

Every sign-in, configuration edit, planned workflow, approval, rejection, directory mutation, credential view, certificate rotation, and host-shell recovery command is recorded in Rollcall's append-only audit log (**Admin › Audit log**).

## Tamper-evident hash chain

- Each row in `audit_event` stores the SHA-256 hash of its canonical fields plus the previous row's `hash` (`prev_hash`).
- PostgreSQL enforces a `BEFORE UPDATE OR DELETE` trigger on `audit_event` that rejects any modification or deletion at the database layer.
- Click **Verify hash chain** in **Admin › Audit log** (or call `GET /api/audit/verify`) at any time to verify every row from genesis to the latest event.

## Searching and exporting

- Filter by actor UPN, target object, action name, or UTC date range.
- Click **Export CSV** to stream matching rows as UTF-8 with BOM (`EF BB BF`, so Excel opens international names cleanly) and ISO 8601 UTC timestamps (`2026-11-03T09:00:00Z`).
- Sensitive credentials (Temporary Access Passes, temporary passwords, tokens, and private keys) are never written to `audit_event`. Viewing a temporary credential records *who* viewed it and *when*, never the credential value.
