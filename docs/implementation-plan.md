# Rollcall: Implementation Plan

Last updated: Oct 21, 2026

This is the build plan for Rollcall v1. It is written for an AI coding agent (or a human developer) who will implement the product milestone by milestone. It turns [`product-spec.md`](product-spec.md) into concrete architecture, conventions, and milestones with testable acceptance criteria. UI, voice, and visual decisions come from [`brand-guide.md`](brand-guide.md).

When this plan and the product spec disagree, the product spec wins on *what* to build and this plan wins on *how*. Raise the conflict in the PR rather than silently picking one.

## Contents

1. [How to use this plan](#1-how-to-use-this-plan)
2. [Technology decisions](#2-technology-decisions)
3. [Repository layout](#3-repository-layout)
4. [System design](#4-system-design)
5. [Cross-cutting rules](#5-cross-cutting-rules)
6. [Testing strategy](#6-testing-strategy)
7. [Milestones](#7-milestones)
8. [Risks and spikes](#8-risks-and-spikes)
9. [Glossary](#9-glossary)

---

## 1. How to use this plan

### Working rules for the implementing agent

- **Work one milestone at a time, in order.** Milestones build on each other. A milestone may span several PRs, but do not start the next one until every acceptance criterion of the current one passes.
- **Each PR is small and reviewable.** Aim for under ~800 changed lines excluding generated code, lockfiles, and snapshots. Title PRs `M<n>: <what>`.
- **Acceptance criteria are the contract.** Every criterion must be demonstrated by an automated test, a CI job, or (only where automation is impossible, marked **[manual]**) a written check recorded in the PR description.
- **Never touch a production tenant.** All live Microsoft 365 testing uses a dedicated test tenant. Live tests are opt-in and read credentials from environment variables, never from committed files.
- **Never add a client secret code path,** a stored admin password, or any mechanism that collects a user's M365 or AD password. This is a product principle, not a preference.
- **Docs ship with code.** If a milestone changes behaviour a user or admin can see, the matching page under `docs/` is updated in the same PR. A milestone is not done until its docs are.
- **Record decisions.** Any choice not settled here goes in an Architecture Decision Record under `docs/adr/` (`NNNN-title.md`, format: Context, Decision, Consequences).
- **Licensing.** Every dependency and base image must be GPLv3-compatible (MIT, BSD, Apache-2.0, LGPL, MPL-2.0, PostgreSQL, GPL). CI enforces this from M0. If a needed component fails the check, stop and write an ADR.

### Definition of done (applies to every milestone)

- All acceptance criteria pass; CI is green on `main` after merge.
- New code has unit tests; new workflow steps have fake-backed integration tests (see [Testing strategy](#6-testing-strategy)).
- No new warnings from the linters/analyzers configured in M0.
- User-facing strings follow the voice rules in `brand-guide.md` §11; visuals use only tokens from `docs/brand/tokens.css`.
- Docs updated; `CHANGELOG.md` has an entry under `Unreleased`.
- Any new Graph permission or directory role is added to `docs/reference/permissions.md` and to the permission manifest (see 4.6).

---

## 2. Technology decisions

These are fixed for v1 unless an ADR changes them.

| Concern | Choice | Why |
| --- | --- | --- |
| Backend language | C# on **.NET 10 (LTS)** | First-class Microsoft Graph SDK; can host PowerShell in-process for Exchange Online; same language as the Windows AD agent, so contracts are shared |
| Web API | ASP.NET Core minimal APIs, OpenAPI document generated at build | Small surface, typed contracts, generated TypeScript client |
| Frontend | **React 19 + TypeScript**, Vite, React Router, TanStack Query | Mainstream, easy for contributors; SPA served by the API container |
| UI components | Headless primitives (Radix UI) styled only with the CSS variables in `docs/brand/tokens.css` | Accessible by default; brand lives in one token file |
| Fonts | Atkinson Hyperlegible Next (400/500/700) and Atkinson Hyperlegible Mono (400/500), self-hosted via `@fontsource/atkinson-hyperlegible-next` and `@fontsource/atkinson-hyperlegible-mono`; never a CDN | Brand guide §5; works on locked-down networks, leaks no IPs |
| Icons | Lucide (`lucide-react`), outline only, using the icon map in brand guide §7 | Brand guide §7; ISC license |
| Database | **PostgreSQL 17** | Spec suggests it; JSONB for previews and step payloads; `SKIP LOCKED` job queue |
| ORM / migrations | EF Core with Npgsql, code-first migrations | Migrations run by `install.sh update` |
| Job queue | Built-in Postgres queue (`SELECT … FOR UPDATE SKIP LOCKED`) | No extra container; jobs need step-level state that lives in Postgres anyway |
| Microsoft Graph | `Microsoft.Graph` SDK v5+, app-only auth via `Azure.Identity` `ClientCertificateCredential` | Certificate-only auth |
| Exchange Online | `ExchangeOnlineManagement` PowerShell module run through the PowerShell SDK (`Microsoft.PowerShell.SDK`) inside the worker, `Connect-ExchangeOnline -Certificate … -AppId … -Organization …` | Mailbox conversion, distribution lists, forwarding, auto-reply, hidden from GAL are not in Graph. See risk R1 for licensing of the module |
| Technician sign-in | OpenID Connect, authorization code flow with PKCE, via `Microsoft.Identity.Web`; confidential client authenticates with the **same certificate** (client assertion) | No secrets anywhere |
| Reverse proxy | **Caddy 2** | Automatic Let's Encrypt; simple config for supplied certs |
| AD agent | .NET 10 Worker Service hosted as a Windows Service, `System.DirectoryServices.AccountManagement` + `System.DirectoryServices.Protocols` | Runs as a gMSA; no stored AD password |
| Install script | POSIX `bash`, `shellcheck` clean | Spec: one command |
| Containers | Multi-arch (`linux/amd64`, `linux/arm64`) images on GHCR, built by GitHub Actions | |
| CI | GitHub Actions | Repo is on GitHub |
| Logging | `Microsoft.Extensions.Logging` with JSON console output; OpenTelemetry traces optional | `docker compose logs` is the main tool for small teams |

### Supported host (decision for the "still to be set" item in the spec)

- **Distributions:** Ubuntu 22.04 and 24.04 LTS, Debian 12 and 13, RHEL / Rocky / AlmaLinux 9.
- **Minimum:** 2 vCPU, 4 GB RAM, 20 GB disk. **Recommended:** 2 vCPU, 8 GB RAM, 40 GB disk.
- **Network:** inbound 443 (and 80 for Let's Encrypt HTTP-01), outbound 443 to Microsoft endpoints.
- **AD agent host:** Windows Server 2019 or later, domain-joined, .NET 10 runtime (bundled in the installer).
- **Browsers:** current and previous major versions of Edge, Chrome, Firefox, and Safari.

---

## 3. Repository layout

```
rollcall/
├── LICENSE                         # GPLv3 full text
├── README.md
├── CHANGELOG.md
├── CONTRIBUTING.md
├── install/
│   ├── install.sh                  # the one-command installer (install | update | backup | restore | uninstall)
│   ├── docker-compose.yml.tmpl
│   └── Caddyfile.tmpl
├── src/
│   ├── Rollcall.sln
│   ├── Rollcall.Contracts/         # DTOs and agent protocol shared by API, worker, agent
│   ├── Rollcall.Domain/            # entities, workflow model, step contract, no I/O
│   ├── Rollcall.Infrastructure/    # EF Core, Graph client, Exchange runner, crypto, queue
│   ├── Rollcall.Api/               # ASP.NET Core host: REST API, OIDC, serves the SPA
│   ├── Rollcall.Worker/            # job worker host: runs workflow steps and schedules
│   ├── Rollcall.Agent/             # Windows service for on-prem AD
│   └── Rollcall.Agent.Installer/   # MSI / PowerShell bootstrap for the agent
├── web/                            # React SPA (Vite)
│   ├── src/
│   │   ├── theme/                  # imports docs/brand/tokens.css; fonts; theme switcher
│   │   ├── dev/components/         # component reference page (dev builds only)
│   │   ├── components/
│   │   ├── features/               # one folder per feature area
│   │   └── api/                    # generated OpenAPI client
│   └── package.json
├── tests/
│   ├── Rollcall.Domain.Tests/
│   ├── Rollcall.Api.Tests/         # integration tests against Postgres (Testcontainers) and fakes
│   ├── Rollcall.Worker.Tests/
│   ├── Rollcall.Agent.Tests/
│   ├── Rollcall.Live.Tests/        # opt-in, against a real test tenant
│   ├── fakes/FakeGraph/            # in-memory Microsoft Graph + Exchange fake (see 6.2)
│   └── e2e/                        # Playwright tests against the running Compose stack
├── docs/
│   ├── product-spec.md
│   ├── brand-guide.md
│   ├── brand/                      # logo SVGs, favicon, tokens.css (single source of design tokens)
│   ├── implementation-plan.md
│   ├── adr/
│   ├── day-1/                      # day-1 deployment guide, one page per step
│   ├── admin/
│   ├── technician/
│   ├── reference/permissions.md
│   ├── troubleshooting.md
│   └── contributing/
└── .github/workflows/
```

---

## 4. System design

### 4.1 Containers

The Compose stack has exactly four services, matching the spec.

| Service | Image | Role | Exposed |
| --- | --- | --- | --- |
| `proxy` | `caddy:2` | TLS termination, HTTP→HTTPS redirect, security headers | 80, 443 on host |
| `app` | `ghcr.io/salmonllama/rollcall-app` | API, OIDC sign-in, serves SPA, agent endpoints | internal 8080 |
| `worker` | `ghcr.io/salmonllama/rollcall-worker` | Runs jobs, schedules, retries; Graph and Exchange calls | none |
| `db` | `postgres:17` | All state | none (internal network only) |

- `app` and `worker` are built from the same codebase; they are separate images so the worker can carry PowerShell and the Exchange module without bloating the API.
- Volumes: `db-data`, `caddy-data`, `rollcall-config` (holds nothing secret except what `.env` points to).
- Both `app` and `worker` read secrets from `/opt/rollcall/.env` (mode `0600`, root-owned) via Compose `env_file`.
- Healthchecks: `app` `GET /healthz` (process up) and `GET /readyz` (DB reachable, migrations current); `worker` writes a heartbeat row every 15 s and the `app` reports it on `/readyz`.

### 4.2 Secrets and cryptography

- `install.sh` generates into `.env`: `POSTGRES_PASSWORD` (32 random bytes, base64), `ROLLCALL_MASTER_KEY` (32 bytes, base64), `ROLLCALL_SETUP_TOKEN` (one-time, 24 bytes, base32).
- **Envelope encryption.** Sensitive values in the DB (certificate private key, Exchange session material if any) are encrypted with AES-256-GCM using a per-record data key; data keys are wrapped with `ROLLCALL_MASTER_KEY`. Store `key_version` so the master key can be rotated by a CLI command.
- **App certificate.** Generated in the setup wizard: RSA 3072 (or ECDSA P-256 if Exchange Online accepts it; confirm in the M0 spike), self-signed, 12-month validity, subject `CN=Rollcall <hostname>`. The private key is generated in-process and stored only encrypted. The public `.cer` is offered for download.
- **No secret ever appears in logs, API responses, audit entries, or error messages.** Add a logging redaction filter and a test that greps captured logs for known secret values.

### 4.3 Data model (core tables)

Names are indicative; the agent may refine them in an ADR.

| Table | Purpose | Key columns |
| --- | --- | --- |
| `tenant` | The one connected tenant | `tenant_id`, `app_client_id`, `primary_domain`, `connected_at` |
| `app_certificate` | Current and previous app certs | `thumbprint`, `not_after`, `encrypted_private_key`, `status` (pending, active, retiring, retired) |
| `portal_user` | Technicians who have signed in | `entra_object_id`, `upn`, `display_name`, `roles[]`, `disabled` |
| `role_template` | Job definitions | `name`, `version`, `definition` (JSONB, schema-validated), `is_archived` |
| `approval_rule` | Which actions need a second person | `action_type`, `required`, `approver_role` |
| `workflow_run` | One onboarding/offboarding/management request | `type`, `status`, `requested_by`, `target_user_ref`, `scheduled_for`, `plan` (frozen preview, JSONB), `plan_hash` |
| `workflow_step` | Ordered steps of a run | `run_id`, `seq`, `kind`, `input`, `status`, `attempts`, `last_error`, `result`, `undo_data` |
| `approval` | Approval decisions | `run_id`, `approver_id`, `decision`, `comment`, `decided_at` |
| `job_queue` | Work items for the worker and the AD agent | `queue` (worker, agent), `payload`, `run_after`, `locked_by`, `locked_until`, `attempts` |
| `scheduled_reversal` | Timed undo (CA travel exclusions, OneDrive access expiry) | `run_id`, `due_at`, `action` |
| `audit_event` | Append-only log | `id`, `occurred_at`, `actor`, `action`, `target`, `details` (JSONB), `outcome`, `prev_hash`, `hash` |
| `ad_agent` | Registered agents | `agent_id`, `name`, `public_key`, `last_seen_at`, `version`, `status` |
| `setting` | Feature toggles and config | `key`, `value` |
| `directory_cache` | Optional short-lived cache of users/groups/licenses for search | `object_id`, `kind`, `data`, `fetched_at` |

**Audit log append-only guarantee:** the application connects with a DB role that has `INSERT, SELECT` only on `audit_event` (no `UPDATE`, `DELETE`, `TRUNCATE`). Each row stores `hash = SHA-256(prev_hash || canonical_json(row))` so tampering through a superuser is detectable; an admin "Verify audit log" action re-walks the chain.

### 4.4 Workflow engine

Every change Rollcall makes, from a single attribute edit to a full offboarding, is a **workflow run** made of **steps**. This is the heart of the product; build it once and well (M3).

**Lifecycle:**

```
Draft → Planned (preview shown) → [AwaitingApproval] → Scheduled | Queued → Running → Succeeded
                                                                              ↘ PartiallyFailed → (retry) → Running
                                                       ↘ Rejected        ↘ Cancelled
```

**Step contract** (C# interface in `Rollcall.Domain`):

```csharp
public interface IWorkflowStep<TInput, TResult>
{
    string Kind { get; }                                    // e.g. "graph.user.assignLicense"
    Task<StepPlan> PlanAsync(TInput input, PlanContext ctx); // read-only: describes the change, detects no-ops
    Task<TResult> ApplyAsync(TInput input, StepContext ctx); // performs the change; must be idempotent
    Task<bool> VerifyAsync(TInput input, TResult result, StepContext ctx); // confirms the change landed
    UndoDescriptor? DescribeUndo(TInput input, TResult result); // null if not reversible
    IReadOnlyList<RequiredPermission> Permissions { get; }  // feeds the permission manifest
}
```

Rules:

- **Plan is read-only and complete.** The preview the technician sees is the concatenation of each step's `StepPlan` (human-readable line, before/after values, no-op flag, warnings). The full plan is frozen into `workflow_run.plan` with a hash.
- **Drift check before apply.** Immediately before running (including after approval or at a scheduled time), the engine re-plans and compares. If the plan changed materially, the run stops in `NeedsReview` and the technician must re-confirm; an approval does not carry over to a different plan.
- **Idempotent apply.** Re-running a succeeded step must be a no-op. Steps check current state before acting (for example, skip adding a member who is already in the group).
- **Retry.** A failed step leaves the run in `PartiallyFailed` with later steps untouched. The UI's **Retry** button resumes from the first failed step. Transient errors (HTTP 429, 503, 504, network) retry automatically with exponential backoff honouring `Retry-After`, up to 5 attempts; permanent errors (403, 400 validation) do not.
- **Dependencies.** Steps run in sequence by default. A step may declare `WaitFor` conditions (for example "user exists in Entra"), which the engine polls with backoff and a timeout (default 45 minutes for directory sync).
- **Undo data.** Steps that remove something (groups, licenses, CA membership) record exactly what they removed in `undo_data`, so offboarding can be reversed and the user page can show "restore".
- **Audit.** The engine writes audit events for: run created, planned, approved/rejected, each step started/succeeded/failed/retried, run finished. Steps never write audit rows directly.
- **Concurrency.** Two runs targeting the same user are serialized (advisory lock on target object id). Worker concurrency defaults to 4 runs.

### 4.5 Technician sign-in and portal roles

- OIDC against `https://login.microsoftonline.com/{tenantId}/v2.0`, single-tenant. Only accounts from the connected tenant can sign in.
- The app registration has a **Web** redirect URI `https://<host>/signin-oidc`. Token validation per `Microsoft.Identity.Web` defaults.
- Session: encrypted, `HttpOnly`, `Secure`, `SameSite=Lax` cookie, 8-hour absolute lifetime, 1-hour idle. CSRF protection on all state-changing endpoints (antiforgery token header).
- **Portal roles** are stored in `portal_user.roles`: `Technician`, `Approver`, `Admin`. A user may have several. A signed-in tenant user with no roles sees an "ask your admin for access" page and can do nothing else.
- Role matrix (enforced server-side by policy, mirrored in UI):

| Capability | Technician | Approver | Admin |
| --- | --- | --- | --- |
| Search users, view user page, dashboard | ✓ | ✓ | ✓ |
| Run onboarding, offboarding, group/license/attribute/MFA changes | ✓ | | ✓ |
| Approve or reject others' requests | | ✓ | ✓ |
| Change CA policy membership | | | ✓ |
| Manage role templates, approval rules, settings, AD agents, certificates | | | ✓ |
| View and export audit log | ✓ (own actions) | ✓ | ✓ |

- **Nobody approves their own request**, regardless of roles.
- **Bootstrap:** the setup wizard (protected by `ROLLCALL_SETUP_TOKEN`) makes the first person to complete Entra sign-in during setup an Admin, then permanently invalidates the setup token.
- **Break-glass:** `install.sh admin-reset <upn>` grants Admin to a UPN from the host shell and writes an audit event. Documented in the admin guide.

### 4.6 Permission manifest

- Each step declares its Graph application permissions, Exchange needs, and directory roles. A build step aggregates them into `permissions.generated.json`, grouped by feature.
- Features can be disabled in Settings. The manifest drives: the permission list in the setup wizard, the Graph PowerShell script the wizard generates (see M2), the "Permissions" health page (compares granted vs required from the token's `roles` claim and the service principal's directory roles), and `docs/reference/permissions.md` (CI fails if the doc is out of date).
- Starting permission set: the table in the product spec. Verify each one against current Microsoft docs during implementation and record any change in an ADR.

### 4.7 AD agent protocol

- **Enrollment:** an Admin creates an agent in Settings, which issues a one-time enrollment token (valid 1 hour) and shows a PowerShell install command containing the portal URL and token. The installer installs the service to run as a specified gMSA. On first start, the agent generates an ECDSA P-256 key pair (private key protected with DPAPI under the service account, machine scope off), and calls `POST /api/agent/enroll` with the token and public key. The portal stores the public key only.
- **Auth:** every agent request carries a short-lived JWT (5 minutes) signed by the agent's private key, `sub = agent_id`. No shared secret is stored on either side.
- **Polling:** `POST /api/agent/jobs/lease` long-polls for up to 30 s and returns at most N jobs with a lease (5 minutes). The agent posts `heartbeat`, `progress`, and `result` per job. Outbound HTTPS only.
- **Job types (v1):** `ad.user.create`, `ad.user.update`, `ad.user.setEnabled`, `ad.user.resetPassword`, `ad.user.move`, `ad.user.removeFromAllGroups`, `ad.group.create`, `ad.group.update`, `ad.group.move`, `ad.group.addMember`, `ad.group.removeMember`, `ad.ou.list`, `ad.user.get`, `ad.group.get`, `ad.search`, `sync.delta`.
- **Scope guard:** Admins configure allowed OUs. The agent itself refuses any write outside allowed OUs, as a second line of defence after the portal check.
- **Entra Connect sync:** `sync.delta` runs `Start-ADSyncSyncCycle -PolicyType Delta`. If the agent is not on the Entra Connect server, it uses PowerShell remoting to the configured sync server; the gMSA must be in `ADSyncOperators` there. Entra Cloud Sync is out of scope for v1 (document it).
- **Versioning:** agent reports its version; the portal shows "update available" and rejects agents below a minimum protocol version with a clear error.

### 4.8 Cloud vs hybrid user handling

Every user is classified at plan time by `onPremisesSyncEnabled`:

| Attribute or action | Cloud-only user | Synced (hybrid) user |
| --- | --- | --- |
| Create account | Graph | AD agent, then `sync.delta`, then wait for the user in Entra |
| Name, title, department, manager, office, phone, employee ID | Graph | AD agent (mastered in AD), then sync |
| Usage location, licenses, MFA methods, TAP, CA membership, cloud groups | Graph | Graph |
| Block sign-in | Graph `accountEnabled=false` | AD disable, then sync; plus Graph session revoke immediately |
| Password reset | Graph | AD agent |
| Groups | Graph (security, M365), Exchange (DLs, mail-enabled security) | AD groups via agent; cloud groups via Graph |

The plan step that would write a mastered-in-AD attribute through Graph for a synced user must fail planning with a clear message. Add a test for it.

### 4.9 Frontend structure

- Routes: `/` Dashboard, `/users`, `/users/:id`, `/onboard`, `/offboard/:id?`, `/runs`, `/runs/:id`, `/approvals`, `/groups`, `/licenses`, `/conditional-access`, `/audit`, `/admin/templates`, `/admin/approval-rules`, `/admin/team`, `/admin/connections`, `/admin/agents`, `/admin/settings`, `/setup/*`, `/help`.
- All data access through the generated OpenAPI client and TanStack Query.

**Design system.** [`brand-guide.md`](brand-guide.md) is the source of truth for everything visual and every user-facing string. The rules below are how the code honours it.

- **Tokens.** `docs/brand/tokens.css` is the only place colors, type sizes, spacing, radii, shadows, durations, and easing are defined, for light and dark. `web/` imports that file directly (Vite alias), so a brand change is a one-file change. Components use only `var(--rc-*)`. A Stylelint rule fails CI on any hex/rgb/hsl color, raw `px` font size, or `box-shadow` literal outside `tokens.css`, and on Microsoft brand blue `#0078D4` anywhere.
- **Theme.** Light, Dark, and System (default) in the user menu, stored per browser; dark is a real theme from the tokens, never an inversion.
- **Fonts.** Self-hosted Atkinson Hyperlegible Next and Mono via `@fontsource`. CSP stays `font-src 'self'`. Mono for every identifier a technician might copy or read aloud (UPNs, emails in tables, TAP codes, passwords, object IDs, thumbprints, commands). Tabular numbers in tables, counts, and dates.
- **Icons.** Lucide only, using the brand guide's icon map for navigation and concepts and its status-icon table for states. A missing icon is drawn on the same grid and added to `docs/brand/icons/`.
- **App shell** (brand guide §6): 240px sidebar collapsible to a 64px rail, nav order Dashboard, Users, Onboarding, Offboarding, Groups, Licenses, Conditional Access, Approvals, Audit log, then Templates and Settings for Admins, with Help and the user menu at the bottom. 56px top bar with global search (shortcut `/`), job activity indicator, and the tenant name in Mono. Breakpoints 640 / 1024 / 1280px; default table rows 44px, Compact 36px.
- **Run states → brand status.** Every run and step state maps to one status from brand guide §7, always shown as icon + word + color:

| Engine state | Brand status (icon) |
| --- | --- |
| Draft, Queued | Queued (`circle-dashed`) |
| Scheduled | Scheduled (`calendar-clock`) |
| AwaitingApproval | Awaiting approval (`hourglass`) |
| Running, Waiting for AD agent | Running (`loader-circle`) |
| NeedsReview, PartiallyFailed with no failed step pending | Needs attention (`triangle-alert`) |
| Failed step, PartiallyFailed, Rejected | Failed (`circle-x`) |
| Cancelled, skipped (no-op) step | Paused or skipped (`circle-pause`) |
| Succeeded | Succeeded (`circle-check`) |

- **Signature components** (brand guide §8), each built once and reused:
  - **Change preview.** Renders any list of `StepPlan`s grouped by target system (Entra ID, Exchange Online, on-prem AD) then by area. Lines start with a Mono operation marker: `+` add (success), `−` remove (danger), `~` change (info), then plain-language text and the exact object name; attribute changes show `old → new`. No-op lines are muted with the reason; lines needing approval carry an "Approval required" badge. A summary bar on top ("12 changes across 3 systems · 2 need approval") and a primary button that repeats the count ("Run 12 changes"). The same component renders the approval view, the run detail, and the audit entry, so they look identical.
  - **Job and step progress.** Vertical step list with status icon, name, target system, and duration; a failed step expands to a plain-language message, the raw Microsoft/AD error in a collapsible Mono block, and **Retry step**. Never one spinner for many steps. Updates announced in a polite live region.
  - **Approval banner.** Full-width info banner on a pending run: requester, time, link to the preview, Approve/Reject for approvers; the requester sees "Waiting for an approver" instead.
  - **Destructive confirmation.** Offboarding, removing a CA exclusion, and bulk removals open a dialog that restates the impact in one sentence and enables the danger button only after the technician types the target's UPN (or the affected count for bulk actions).
  - Buttons (Primary, Secondary, Ghost, Danger; one primary per view; labels name the action, never "OK"/"Submit"), form fields, status badge, tables, toasts, empty states, and credential display (TAP/password at `--rc-text-lg` Mono, grouped in fours, with Copy).
  - All of these appear on a **component reference page** at `/dev/components` in development builds, in every state, light and dark. Visual snapshot tests run against this page.
- **Copy.** Every user-facing string follows brand guide §11: sentence case, active voice, name the object and count, errors say what happened and what to do, no "Oops", "Success!", or "Are you sure?". Use the word list (for example "block sign-in" for cloud users, "disable" only for AD; "remove" not "delete"). Strings live in one resource file per feature so they can be reviewed together. Dates show as "Nov 3, 2026" in the UI and ISO 8601 in exports.
- **Motion.** 150ms `--rc-duration`, opacity/transform only; the Running spinner is the only loop and stops under `prefers-reduced-motion`.
- **Accessibility:** WCAG 2.2 AA. Every page passes `axe-core` in Playwright with zero serious or critical violations in light and dark. Full keyboard operation, visible 2px `--rc-focus` ring, dialogs trap and return focus, no information conveyed by color alone, text usable at 200% zoom, and the approval pages work at 320px wide.

---

## 5. Cross-cutting rules

- **API conventions.** JSON, `camelCase`, RFC 9457 problem details for errors, cursor pagination (`?cursor=&limit=`), `ETag`/`If-Match` on editable resources (templates, settings).
- **Error messages** shown to users say what happened, why (if known), and what to do next, in the brand voice (brand guide §11: "Couldn't convert the mailbox to shared. Exchange Online reported that the mailbox is on litigation hold. Remove the hold or skip this step, then retry."). Raw Graph or AD errors appear only in the collapsible Mono block of the step progress component.
- **Graph usage.** Use `$select` on every read. Respect throttling (`Retry-After`). Batch with `$batch` where it reduces calls (group membership reads, bulk attribute edits). Never page through all users on a hot path; use `$search`/`$filter` with `ConsistencyLevel: eventual`.
- **Time.** Store UTC; display in the browser's time zone; schedule start/leave dates in a tenant-default time zone set in Settings (scheduled for 06:00 local by default, configurable).
- **Feature flags.** Each feature area (Licenses, CA, MFA, Mailbox, OneDrive, Hybrid AD) can be turned off in Settings; off means hidden in UI, rejected by API, and its permissions dropped from the manifest.
- **Observability.** Structured logs include `run_id`, `step_id`, `actor`, `correlation_id`; Graph `request-id` and `client-request-id` are captured on errors for Microsoft support cases.
- **Security headers** from Caddy: HSTS (1 year), `Content-Security-Policy` (self only, no inline scripts), `X-Content-Type-Options`, `Referrer-Policy: same-origin`, `frame-ancestors 'none'`.

---

## 6. Testing strategy

### 6.1 Layers

| Layer | Tooling | Runs in CI |
| --- | --- | --- |
| Unit (domain, plan logic, template resolution) | xUnit, FluentAssertions; Vitest + Testing Library for web | Every PR |
| Integration (API + DB + engine + fakes) | `WebApplicationFactory`, Testcontainers PostgreSQL, FakeGraph | Every PR |
| End-to-end (browser, full stack) | Playwright against `docker compose` with FakeGraph and a fake OIDC provider | Every PR (smoke), nightly (full) |
| Installer | `bats` tests + a job that runs `install.sh` in fresh Ubuntu 24.04, Debian 12, and Rocky 9 VMs/containers | Every PR touching `install/`, nightly |
| Agent | Unit tests against an `IDirectory` abstraction with an in-memory fake; contract tests against the portal API | Every PR (Windows runner) |
| Live tenant | `Rollcall.Live.Tests` against a test tenant | Manual or nightly with repository secrets; never on forks |
| Live AD | Scripted checklist against a lab domain **[manual]** | Before each release |

### 6.2 FakeGraph

Build in M3, extend with each feature. An in-memory HTTP server implementing the Graph endpoints Rollcall uses (users, groups, members, licenses/subscribedSkus, assignLicense, authentication methods, TAP, CA policies, revokeSignInSessions, drive permissions) plus a fake Exchange runner implementing the cmdlets used. It must support: injecting failures (429 with `Retry-After`, 403, 500) per endpoint, simulating sync delay (user appears after N seconds), and asserting the call log. The Graph SDK is pointed at it by base URL.

### 6.3 Fake OIDC

A tiny OIDC provider for tests and local dev that issues tokens for seeded users, so E2E tests can sign in as Technician, Approver, or Admin.

### 6.4 Local development

`make dev` (or `./dev.sh`) starts Postgres, FakeGraph, and fake OIDC in Compose, runs the API and worker with hot reload, and the Vite dev server. A seeded dataset gives ~200 users, ~40 groups, 5 SKUs, 6 CA policies.

---

## 7. Milestones

Overview:

| # | Milestone | Outcome |
| --- | --- | --- |
| M0 | Foundations | Repo, CI, licensing checks, ADRs, Exchange spike |
| M1 | Installable stack | `install.sh` brings up four healthy containers over HTTPS; update, backup, restore |
| M2 | Setup wizard, tenant connection, sign-in | Certificate generated, tenant connected, admins sign in with Entra, roles work |
| M3 | Workflow engine, preview, approvals, audit | The core engine every feature uses, proven with one simple action |
| M4 | Directory views and dashboard | User search, user page, groups, licenses views, dashboard |
| M5 | Cloud management features | Attributes, groups, distribution lists, licenses, MFA and TAP |
| M6 | Role templates and cloud onboarding | Template editor, new-hire flow, scheduling, welcome pack |
| M7 | Cloud offboarding | Full offboarding flow including shared mailbox, OneDrive handover, reversal |
| M8 | Conditional Access management | Include/exclude edits with safety rails and timed exclusions |
| M9 | Hybrid AD agent | Agent install and enrollment, AD users/groups/OUs, sync, hybrid on/offboarding |
| M10 | Operations and hardening | Certificate rotation, audit export and verification, health pages, security review |
| M11 | Documentation and v1.0 release | Complete doc set, day-1 guide walkthrough on a clean server, release |

Branding work is spread through the milestones rather than saved for the end: tokens, fonts, theme, and app shell in M2; change preview, step progress, approval banner, and destructive confirmation in M3 and M7; the remaining raster and social-preview assets and a full consistency audit in M10; the docs site styling in M11.

---

### M0: Foundations

**Goal:** an empty but well-run repository that every later milestone can build on.

**Tasks**

- Add `LICENSE` (GPLv3 text), `README.md` (logo from `docs/brand/rollcall-logo.svg`, pitch, status, links, and the Microsoft non-affiliation line from brand guide §2 as its footer), `CONTRIBUTING.md` (DCO sign-off, coding standards, PR rules from section 1), `CODE_OF_CONDUCT.md`, `SECURITY.md` (private vulnerability reporting).
- Create the solution and projects from section 3 with placeholders that compile; create the `web/` Vite app.
- Configure analyzers: .NET (`TreatWarningsAsErrors`, nullable enabled, `dotnet format` check), ESLint + Prettier + TypeScript strict for `web/`, `shellcheck` for `install/`.
- CI workflow: build, test, lint for .NET and web; license compliance check (`dotnet-project-licenses` or equivalent for NuGet, `license-checker` for npm) failing on any non-GPLv3-compatible license; container image build (no push on PRs).
- ADR 0001 records the technology decisions in section 2.
- **Spike S1 (Exchange Online on Linux):** prove in a throwaway branch that the worker container can run `Connect-ExchangeOnline` with a certificate loaded from memory (not a file on disk if possible) and run `Get-Mailbox` and `Set-Mailbox -Type Shared` against a test tenant. Measure session start time and memory. Decide how the module is obtained (see risk R1). Write ADR 0002 with the result.
- **Spike S2 (certificate algorithm):** confirm RSA 3072 vs ECDSA works for both Graph client assertion and Exchange app-only; record in ADR 0002.

**Acceptance criteria**

1. `git clone` then `dotnet build src/Rollcall.sln` and `npm ci && npm run build` in `web/` succeed with zero warnings.
2. CI runs on every PR and blocks merge on failure of build, tests, lint, or license check.
3. Introducing a dependency with a GPL-incompatible license (test by adding a known SSPL or proprietary package in a scratch PR) makes the license job fail. **[manual, recorded in PR]**
4. `LICENSE` contains the unmodified GPLv3 text; every source file template includes the SPDX header `SPDX-License-Identifier: GPL-3.0-or-later`, and a CI check enforces it.
5. ADR 0001 and ADR 0002 are merged; ADR 0002 states the Exchange approach with measured session time and memory, and whether the module is bundled or fetched.

---

### M1: Installable stack

**Goal:** a single command turns a clean Linux host into a running Rollcall stack served over HTTPS.

**Tasks**

- `install.sh` subcommands: `install` (default), `update`, `backup`, `restore <archive>`, `status`, `logs`, `admin-reset <upn>` (stub until M2), `uninstall` (keeps data unless `--purge`).
- Install flow per spec: check OS and architecture, install Docker Engine and Compose plugin from Docker's official repos if missing, prompt for hostname and TLS mode (Let's Encrypt with an email, or paths to a supplied cert and key), generate `.env`, render `docker-compose.yml` and `Caddyfile` into `/opt/rollcall`, pull images, start, wait for `/readyz`, print URL and setup token.
- Non-interactive mode: every prompt has a flag (`--hostname`, `--tls letsencrypt|custom`, `--email`, `--cert`, `--key`, `--yes`) for automation and CI.
- Pin image tags to the installer's release version; `update` fetches the latest installer, pulls the matching images, takes an automatic backup, runs migrations (the `app` container runs EF migrations on start behind a DB advisory lock), and restarts.
- `backup` writes `rollcall-backup-<timestamp>.tar.gz` containing a `pg_dump` (custom format), `.env`, and rendered config, mode `0600`. `restore` stops the stack, restores, and starts it. Warn that the archive contains the master key.
- Installer output follows brand guide §13: print each step, then `✓` or `✕` with a one-line result; green/red only on those marks; plain output when `NO_COLOR` is set or stdout is not a TTY; on failure print what failed, the likely cause, and the next command; finish with the URL on its own line and "Open https://<host> to finish setup."
- `app` serves a placeholder page with the Rollcall logo (`docs/brand/`) and favicon, and the `/healthz` and `/readyz` endpoints. `worker` writes heartbeats.
- Publish images to GHCR on tags (`v*`) and on `main` as `edge`.

**Acceptance criteria**

1. On fresh Ubuntu 24.04, Debian 12, and Rocky 9 hosts, `curl -fsSL <installer-url> | sudo bash -s -- --hostname rc.test --tls custom --cert c.pem --key k.pem --yes` completes with exit code 0 in under 5 minutes (excluding image download time) and prints the portal URL and setup token. (CI job.)
2. After install, `docker compose ps` shows exactly four services, all `healthy`; `curl https://rc.test/readyz` returns 200 with `{"db":"ok","migrations":"current","worker":"ok"}`.
3. `curl http://rc.test` redirects to HTTPS; response headers include HSTS and the CSP from section 5.
4. `.env` exists with mode `0600`, owner root, and contains non-empty, distinct random values; re-running `install` on an installed host does not regenerate secrets and exits with a message pointing to `update`.
5. The `db` service publishes no host ports (`docker compose port db 5432` returns nothing).
6. `install.sh backup` then `install.sh uninstall --purge` then fresh `install` then `install.sh restore <archive>` results in the same data (verified by a row count and checksum of a seeded table).
7. `install.sh update` from version N-1 to N runs migrations and the stack returns to healthy; a failed migration leaves the previous version running and prints the backup path. (Tested with a deliberately broken migration in CI.)
8. `shellcheck install/install.sh` passes; `bats` tests cover argument parsing and every subcommand's help text.
9. With `NO_COLOR=1`, installer output contains no ANSI escape sequences (bats test); a forced failure (for example port 443 in use) prints the failed step, the likely cause, and a next command.
10. Docs: `docs/day-1/01-prerequisites.md` and `docs/day-1/02-install.md` written, each with the expected result and how to check it.

---

### M2: Setup wizard, tenant connection, and sign-in

**Goal:** an admin goes from the setup URL to signed in with Entra, with Rollcall connected to the tenant using only a certificate.

**Tasks**

- Branding foundation (brand guide §§3–8): import `docs/brand/tokens.css`; bundle the Atkinson Hyperlegible Next and Mono fonts via `@fontsource`; Light/Dark/System theme switch; the app shell from 4.9 (sidebar with logo and nav, rail collapse, top bar with search and tenant name in Mono, Help and user menu); buttons, form fields, status badge, alerts, toasts, empty states, and the credential display; the `/dev/components` reference page; the Stylelint token rule; favicon and PNG/PWA icons generated from the SVGs at build time.
- Setup wizard at `/setup`, gated by the setup token (entered once, stored in a short-lived cookie). Steps:
  1. Welcome and checklist (what you need: Global Administrator for consent, the tenant's primary domain).
  2. Generate certificate (see 4.2); show thumbprint and expiry; download `.cer`.
  3. App registration: two paths shown side by side. **(a) Guided script:** the wizard generates a Microsoft Graph PowerShell script (from the permission manifest) that the admin runs in Azure Cloud Shell or locally; it creates the app, uploads the cert, adds the Web redirect URI, adds the application permissions, grants admin consent, and assigns directory roles to the service principal, then prints the tenant ID and client ID. **(b) Manual:** numbered portal steps with the exact permission list and redirect URI, with copy buttons.
  4. Enter tenant ID and client ID; Rollcall validates by acquiring an app-only token and calling `GET /organization` and lists any missing permissions or roles from the manifest.
  5. Sign in as the first admin with Entra (OIDC); that user becomes Admin; setup token is invalidated.
  6. Optional: enable Hybrid AD (links to M9 flow; hidden until M9 ships).
- Fake OIDC provider (6.3) for tests and local dev.
- Technician sign-in and session handling per 4.5; role-based authorization policies; "no access" page.
- Admin › Team page: list portal users who have signed in, plus "invite" by UPN or by picking from the tenant directory (pre-creates the `portal_user` with roles); edit roles; disable access.
- `install.sh admin-reset <upn>` implemented.
- Connections health card on Admin › Connections: tenant, certificate expiry, permissions granted vs required, directory roles present.

**Acceptance criteria**

1. Visiting `/setup` without the token, or after setup completes, returns 404; the setup token from `.env` no longer works after step 5 (asserted in integration test).
2. The certificate private key never leaves the server: no API response contains it (integration test inspects every wizard response), and the DB column is ciphertext that does not contain the PEM header.
3. Against a live test tenant **[manual, recorded]**, the generated PowerShell script creates a working app registration, and step 4 reports all permissions and roles present.
4. A tenant user without roles who signs in sees the "ask your admin" page and receives 403 from every API except `/api/me`.
5. A user from a different tenant cannot sign in (token validation test with a foreign `tid`).
6. Role matrix in 4.5 is covered by an authorization test per endpoint group (generated table test: role × endpoint → expected status).
7. The app shell and wizard pass axe-core with zero serious/critical issues in light and dark mode, and a visual snapshot test exists for each wizard step and for each component on `/dev/components`.
8. The built SPA makes no request to any third-party host (Playwright asserts every network request is same-origin); fonts render as Atkinson Hyperlegible (computed style check); the Stylelint token rule passes and fails on a planted `#0078D4`.
9. The theme switch persists across reloads, System follows `prefers-color-scheme`, and the sidebar collapses to the rail below 1024px and to a drawer below 640px.
10. Docs: `docs/day-1/03-app-registration.md`, `04-directory-roles.md`, `05-first-admin.md`; `docs/reference/permissions.md` generated from the manifest; CI fails if it is stale.

---

### M3: Workflow engine, preview, approvals, and audit

**Goal:** the engine from section 4.4, the shared preview and run-status UI, approvals, and the audit log, proven end to end with one simple action ("Edit job title").

**Tasks**

- Domain model, step contract, plan freezing with hash, drift check, step execution, retries, backoff, `WaitFor`, undo data, per-target serialization.
- Postgres job queue with leasing, visibility timeout, and dead-lettering after max attempts; worker host with graceful shutdown (finishes or releases leases within 30 s on `SIGTERM`).
- Scheduler: runs whose `scheduled_for` is due are queued; scheduled reversals fire when due.
- Approval rules (Admin › Approval rules): per action type, required yes/no; defaults ON for offboarding, CA changes, and template-driven onboarding of users with admin roles; OFF otherwise.
- Approvals inbox (`/approvals`) and the approval banner (4.9) showing the frozen preview, requester, comment box, approve/reject. Rejecting requires a non-empty comment (enforced by the API, not only the UI); the comment is shown to the requester on the run and stored in the audit log. Approving may include an optional comment.
- Change preview and job/step progress components exactly as specified in 4.9 and brand guide §8.
- Audit log table with hash chain; `/audit` page with filters (actor, action, target, date range, outcome); technicians see only their own actions.
- FakeGraph (6.2) foundation; fake Exchange runner interface.
- Retrofit FakeGraph-backed E2E coverage for the M2 setup wizard (criteria 13 and 14 below), since FakeGraph does not exist until this milestone.
- First step type: `graph.user.updateAttributes` (title only for now), wired to a "Edit job title" action on a stub user page.

**Acceptance criteria**

1. Editing a user's title shows a preview with before → after; submitting creates a run that succeeds and FakeGraph's call log shows exactly one `PATCH /users/{id}` with only `jobTitle`.
2. Planning a change that is already true shows it as a no-op and submitting makes zero write calls.
3. With approval required for the action, a Technician's request waits in `AwaitingApproval`; the same user cannot approve it (403); a different Approver can; rejecting with an empty or whitespace-only comment returns 400 and leaves the run in `AwaitingApproval`; a rejection with a comment ends the run with no Graph writes and shows the comment on the run and in its audit event.
4. If the target's title changes in Graph between approval and execution, the run stops in `NeedsReview` and makes no writes (drift test).
5. FakeGraph returning 429 with `Retry-After: 2` twice then 200 results in success after ≥4 s and three attempts; returning 403 fails immediately with a plain-language error and a Retry button; after fixing the fake, Retry succeeds and the run shows the full attempt history.
6. Killing the worker mid-run (`docker kill`) and restarting it resumes the run without duplicating completed steps.
7. Two concurrent runs on the same user execute one after the other (asserted by timestamps); runs on different users execute in parallel.
8. The audit log records every lifecycle event in 4.4 with actor, target, and outcome; the application's DB role gets `permission denied` on `UPDATE`/`DELETE` of `audit_event` (integration test); altering a row as superuser makes "Verify audit log" report the first broken row.
9. A scheduled run set for T+2 minutes executes within 60 s after T+2 minutes.
10. The change preview renders `+`/`−`/`~` markers in Mono with the right status colors, groups by system then area, shows the summary bar and a count-bearing primary button ("Run 1 change"), and every run/step state uses the icon + word from the 4.9 mapping table. Snapshots on `/dev/components` cover: no-op, add, remove, change, approval required, awaiting approval (approver and requester views), failed with retry, succeeded; all pass axe-core.
11. The approvals inbox and a pending run's approval banner are fully usable at 320px wide (Playwright mobile viewport).
12. Docs: `docs/admin/approval-rules.md`, `docs/technician/previews-and-retries.md`, `docs/admin/audit-log.md`.
13. With FakeGraph and fake OIDC (E2E), a test walks the whole M2 setup wizard from the setup URL to the dashboard as Admin with no manual steps.
14. Wizard step 4 with a deliberately missing permission (FakeGraph token without `User.ReadWrite.All`) shows that permission by name and which features it affects.

---

### M4: Directory views and dashboard

**Goal:** technicians can find anyone and see everything about them on one page.

**Tasks**

- User search (`/users`): type-ahead over display name, UPN, mail, employee ID; filters for department, enabled/disabled, cloud/synced, licensed/unlicensed. Uses Graph `$search` with `ConsistencyLevel: eventual`.
- User page (`/users/:id`): attributes, account status, cloud/synced badge, manager, groups (direct, with type), licenses (direct vs group-based), registered MFA methods (types only, never secrets), CA policies that apply (computed from policy include/exclude users and groups, with "may apply" when roles, apps, or conditions make it uncertain), recent runs, and change history from the audit log.
- Groups list and group page (members, owners, type, source cloud/AD).
- Licenses page: SKUs with friendly names, total/consumed/available seats, list of licensed disabled accounts with a bulk "remove licenses" action (implemented in M5).
- Dashboard: upcoming onboardings and offboardings (from scheduled runs), pending approvals, recent failures, unused licenses (available seats and licenses on disabled accounts), certificate expiry warning, agent status (from M9).
- Short-lived `directory_cache` for list views (TTL 5 minutes, manual refresh button).

**Acceptance criteria**

1. With the seeded 200-user FakeGraph, search returns matches for partial name, UPN, and employee ID within 1 s (p95 measured in an integration test).
2. The user page shows all sections listed above; for a group-licensed SKU it shows the source group and does not offer direct removal.
3. Every Graph read uses `$select` (FakeGraph asserts and fails the test on any read without it).
4. CA "applies to this user" is correct for: direct include, group include, direct exclude overriding group include, "All users" with exclusion. Unit-tested.
5. Dashboard tiles each link to a filtered list and show the correct counts for the seeded data.
6. A user with no MFA methods, a synced user, a disabled user, and a guest all render without errors and with the right badges.
7. All new pages pass axe-core and work at 1280 px and 1024 px widths; tables are keyboard-navigable.
8. Docs: `docs/technician/finding-users.md`, `docs/technician/dashboard.md`.

---

### M5: Cloud management features

**Goal:** every management feature in the spec except CA and hybrid AD, as previewed, audited workflow runs.

**Tasks**

- **Attributes:** edit title, department, manager, office, phone, mobile, employee ID, usage location, company name, and admin-configured extension or custom security attributes; single and bulk (CSV upload or multi-select with a common value). Synced users: block AD-mastered attributes with the message from 4.8 (until M9 routes them through the agent).
- **Groups:** add/remove members for security and M365 groups (Graph) and distribution lists and mail-enabled security groups (Exchange: `Add-DistributionGroupMember`/`Remove-DistributionGroupMember`); bulk add many users to one group or one user to many groups; "copy groups from another user".
- **Licenses:** assign/remove SKUs, optionally with disabled service plans; set usage location first if missing; block assignment when no seats are available, with the count; bulk remove from disabled accounts.
- **MFA:** list methods; remove a specific method (phone, Authenticator, FIDO2, software OATH, Windows Hello where supported); "require re-registration" (remove all methods and revoke sessions); issue a Temporary Access Pass (lifetime and one-time per tenant policy), shown once and never stored.
- **Sessions and password:** revoke sessions; reset password (cloud users) with a generated password shown once, force change at next sign-in.
- Exchange runner from spike S1 made production-ready: session pooling (one warm session, recycled every 30 minutes or on error), per-cmdlet timeout, output parsed into typed results, errors mapped to plain language.

**Acceptance criteria**

1. Each action above has a step type with Plan/Apply/Verify, a FakeGraph-backed integration test for success, no-op, and a permanent failure, and an entry in the permission manifest.
2. Bulk-adding 50 users to a group uses Graph `$batch` (≤ 3 HTTP requests to FakeGraph for writes) and shows a 50-line preview collapsed to a summary with a "show all" toggle.
3. Assigning a SKU with zero available seats fails at plan time with "No <friendly SKU name> licenses available (0 of N)".
4. Assigning a license to a user without usage location adds a "set usage location to <tenant default>" step to the preview automatically.
5. A TAP is displayed once in the credential display (Mono, `--rc-text-lg`, grouped in fours, Copy button); it is absent from the DB, logs, and audit details (test searches all three for the value).
6. Removing a user from a distribution list issues `Remove-DistributionGroupMember` through the Exchange runner fake with `-Confirm:$false` and the correct identity.
7. Exchange runner: when the session dies mid-command, the step retries once on a fresh session and succeeds (fake test); memory of the worker stays under 1 GB with 20 sequential Exchange commands **[measured, recorded]**.
8. Editing an AD-mastered attribute on a synced user is refused at plan time with the explanatory message.
9. Against a live test tenant **[manual, recorded]**: assign/remove license, add/remove M365 group and DL member, issue TAP, remove an MFA method, reset password.
10. Docs: one technician guide page per area (`attributes`, `groups`, `licenses`, `mfa`), with screenshots captured by Playwright.

---

### M6: Role templates and cloud onboarding

**Goal:** an Admin defines what a job gets; a Technician onboards a cloud-only new hire from a form in under two minutes.

**Tasks**

- Template schema (JSON Schema, versioned): default attributes (department, title, company, office, usage location), UPN and display-name patterns (for example `{first}.{last}@{domain}` and `{first} {last}`; no collision rule, see below), groups (cloud and, later, AD), licenses with disabled plans, MFA setup (TAP or one-time password; TAP lifetime), account type (cloud or hybrid; hybrid hidden until M9), target OU (hybrid), mailbox settings (time zone, language), optional manager notification text.
- Template editor (`/admin/templates`): form-based, live validation against the tenant (groups and SKUs exist), version history, archive, duplicate, "preview for a sample user".
- New-hire form: first name, last name, department, title, manager (directory picker), start date, role template, and overrides for any template field the template marks as overridable.
- Collisions: if the generated UPN, mail nickname, or display name already exists in Entra ID (or, for hybrid, the `sAMAccountName` or UPN exists in AD), planning stops with the run in `NeedsReview` and the preview flags the conflicting value and the existing object it clashes with. Rollcall never invents an alternative (no numeric suffixes or middle initials); the technician enters a different value by hand, which is re-checked before the run can continue.
- Onboarding plan: create user (disabled until start date if scheduled with "enable on start date" option), set attributes, set manager, add groups, set usage location, assign licenses, wait for mailbox provisioning, set mailbox regional settings, issue TAP or password on start date.
- Welcome pack: printable page and PDF with username, temporary credential (TAP or one-time password), first sign-in steps, and MFA setup steps; shown once to the technician. Admins customize it in Settings › Welcome pack: editable intro text, an uploaded company logo or header image, or a full letterhead (a PDF or image used as the page background, with margins set for the content area), and a toggle to remove all Rollcall branding so the pack carries only the organization's own. The default, with nothing customized, is styled per `brand-guide.md`.
- Scheduled onboarding: account created immediately (so mailbox and licenses are ready) but sign-in blocked until the start date, when the TAP is issued and sign-in enabled; or fully scheduled creation, per template setting.

**Acceptance criteria**

1. A template referencing a group or SKU that does not exist fails validation with the item's name; a template is versioned, and runs record the template version used.
2. Onboarding "Jane Doe" with the "Sales associate" seed template produces a preview listing account creation, every attribute, each group, each license, and the credential step, and on submit FakeGraph ends with exactly the expected user state (snapshot compared).
3. Collisions: with `jane.doe@` taken, or the display name "Jane Doe" already in use, the preview flags the conflict and names the existing user, the run cannot be submitted, and no alternative value is proposed; after the technician enters a unique value by hand the plan re-checks it and continues. Tested for UPN, mail nickname, and display name.
4. Scheduling for a future date creates the account blocked, and on the date (time-travel test with injectable clock) enables sign-in and issues the TAP; the dashboard lists the upcoming onboarding until then.
5. Failure injected at "assign licenses" leaves the user created with groups, shows the failed step with Retry, and after Retry the end state equals the no-failure snapshot (no duplicate user, no duplicate memberships).
6. The welcome pack PDF renders with Rollcall branding by default; with a custom header image or letterhead uploaded and Rollcall branding turned off, it contains the organization's header or letterhead and no Rollcall logo, name, or footer (text and image assertions on the PDF). It contains the credential only on first view, and a second view shows "credential already shown; issue a new one".
7. E2E: Technician completes the form-to-success flow in ≤ 10 interactions (counted in the Playwright test) on the seed template.
8. Against a live test tenant **[manual, recorded]**: a full onboarding results in a usable account that can sign in with the TAP and has a mailbox.
9. Docs: `docs/admin/role-templates.md`, `docs/technician/onboarding.md`, `docs/day-1/07-first-template.md`.

---

### M7: Cloud offboarding

**Goal:** an offboarding is fully and safely completed, everything removed is recorded, and can be reversed.

**Tasks**

- Offboarding form: pick user, leave date/time or "now", options with template-driven or tenant defaults:
  - Mailbox: convert to shared, forward to (user picker) with/without keeping a copy, auto-reply text (internal/external), grant manager Full Access (and optionally Send As), hide from address lists.
  - OneDrive: grant manager access for N days (default 30) then remove access; show tenant OneDrive retention period from SharePoint settings if readable, otherwise a note.
  - Licenses: remove all (after mailbox conversion) or keep specific ones.
- Offboarding plan order (enforced by the engine; tested):
  1. Block sign-in (`accountEnabled=false`)
  2. Revoke sessions
  3. Reset password to a random value not shown to anyone
  4. Remove MFA methods
  5. Mailbox: convert to shared, wait until `RecipientTypeDetails = SharedMailbox`; set forwarding, auto-reply, manager permissions, hide from GAL
  6. OneDrive: grant manager access; create scheduled reversal for the expiry
  7. Remove group memberships (cloud groups, DLs) with undo data
  8. Remove licenses with undo data, only after step 5 verified
  9. Set attributes (for example clear manager, prefix description "Left <date>") as configured
- Warnings in the preview: mailbox over 50 GB (shared mailbox without a license limit), litigation hold present, user owns groups or Teams (list them), user is a member of privileged directory roles (block unless an Admin confirms).
- Reversal: "Restore user" action on a completed offboarding run re-enables sign-in, restores recorded groups and licenses, and leaves the mailbox as shared (with a note) by default.

**Acceptance criteria**

1. The plan order above is enforced; a unit test fails if license removal is ever scheduled before the shared-mailbox verify step.
2. With FakeGraph/fake Exchange, an immediate offboarding ends with: account disabled, sessions revoked, no MFA methods, mailbox shared, forwarding and auto-reply set as chosen, manager has Full Access, hidden from GAL, no group memberships, no licenses; every removed item is in `undo_data`.
3. If mailbox conversion fails, licenses are not removed and the run is `PartiallyFailed` with a message explaining why licenses were kept.
4. A user over 50 GB mailbox or with litigation hold shows the warning in the preview; a user holding a privileged directory role cannot be offboarded by a Technician (403 with explanation) and requires an Admin to confirm.
5. Approval is required by default; submitting requires the destructive confirmation dialog, and the danger button stays disabled until the exact UPN is typed (E2E); a scheduled offboarding executes at the leave time (injectable clock) and the dashboard lists it as an upcoming offboarding.
6. OneDrive access is granted and a scheduled reversal removes it after N days (time-travel test); the audit log records both.
7. "Restore user" returns groups and licenses to the pre-offboarding set (snapshot equality, excluding the mailbox type), with its own preview and approval.
8. Against a live test tenant **[manual, recorded]**: offboard a pilot user end to end; confirm in the Exchange admin center that the mailbox is shared and the license is freed.
9. Docs: `docs/technician/offboarding.md`, `docs/day-1/08-test-onboarding-offboarding.md`.

---

### M8: Conditional Access management

**Goal:** Admins manage who each CA policy includes or excludes, safely, including temporary exclusions with an expiry.

**Tasks**

- CA list: all policies with state (on, off, report-only), and a summary of included/excluded users and groups. Read-only for everything except user and group include/exclude lists; other conditions are shown, never edited.
- Edit include/exclude users and groups for a policy; add a user to an exclusion **with an expiry** (for example travel), which creates a scheduled reversal.
- From the user page: "CA policies for this user" with "add temporary exclusion".
- Safety rails, evaluated at plan time:
  - Refuse any change that would leave a policy with no include target.
  - Warn when excluding a user from a policy that requires MFA or blocks legacy auth.
  - Refuse removing the configured break-glass accounts/group from exclusions (configurable in Settings).
  - Always require approval for CA changes (cannot be turned off below "required" for policies in state "on").
- Use `ETag`-style concurrency: if the policy changed since plan, drift check stops the run.

**Acceptance criteria**

1. Only Admins can change CA membership; Technicians and Approvers get 403 (authorization test). Removing an exclusion goes through the destructive confirmation dialog.
2. A PATCH to a policy changes only `conditions.users` (FakeGraph asserts the request body contains no other properties).
3. Each safety rail has a test: removing the last include is refused; removing a break-glass exclusion is refused; excluding from an MFA policy shows the warning.
4. A temporary exclusion with expiry T is removed automatically within 5 minutes after T (injectable clock); if the user had been excluded permanently by someone else in the meantime, the reversal leaves that permanent exclusion in place and logs why.
5. A policy edited outside Rollcall between plan and apply causes `NeedsReview`, with no write.
6. Against a live test tenant **[manual, recorded]**: add and expire a temporary exclusion on a report-only policy.
7. Docs: `docs/admin/conditional-access.md` including the safety rails and the break-glass setting.

---

### M9: Hybrid AD agent

**Goal:** hybrid shops manage AD users, groups, and OUs from Rollcall, and hybrid onboarding and offboarding work end to end.

**Tasks**

- `Rollcall.Agent` Windows service per 4.7: enrollment, signed-JWT auth, long-poll leasing, heartbeat, structured logging to the Windows Event Log and a rolling file, self-reported version.
- `Rollcall.Agent.Installer`: signed MSI or a PowerShell bootstrap script (`install-agent.ps1 -PortalUrl … -Token … -ServiceAccount 'DOMAIN\rollcall-gmsa$'`) that installs the service under the gMSA, verifies `Test-ADServiceAccount`, and starts it. Document the commands to create the gMSA and delegate rights (create/delete/modify users and groups, reset password, move objects) on the allowed OUs only.
- Admin › Agents: create enrollment command, list agents with status (connected, last seen, version), revoke an agent, configure allowed OUs (picked from `ad.ou.list`), Offboarding OU, default new-user OU, and the Entra Connect server name.
- AD step types for every job type in 4.7, with Plan (via `ad.*.get`) / Apply / Verify, routed through `job_queue` queue `agent`.
- Hybrid routing in onboarding, offboarding, attributes, and groups per table 4.8.
- Hybrid onboarding: AD create in template OU → set attributes → AD groups → `sync.delta` → `WaitFor` user in Entra (match on `onPremisesSamAccountName`/UPN, default timeout 45 min) → usage location, licenses, cloud groups, MFA/TAP.
- Hybrid offboarding: Graph revoke sessions immediately → AD disable → AD password reset → remove AD groups (undo data) → move to Offboarding OU → `sync.delta` → cloud steps from M7 (mailbox, licenses, cloud groups, MFA).
- AD groups management: create group in an allowed OU, change scope/type, rename, move between OUs, add/remove members.
- Dashboard and Connections show agent health; runs needing the agent while it is offline wait (status "Waiting for AD agent") instead of failing, up to a configurable timeout.

**Acceptance criteria**

1. Enrollment: a token works once and expires after 1 hour; a revoked agent's next request gets 401; an agent JWT signed with a different key gets 401; the portal DB contains only the agent's public key (integration tests).
2. The agent opens no listening ports (`Get-NetTCPConnection -State Listen -OwningProcess <pid>` is empty) **[manual, recorded]**; all traffic is outbound HTTPS to the portal.
3. The agent refuses a write outside allowed OUs even if the portal sends one (agent unit test with a forged job).
4. With the in-memory `IDirectory` fake, every AD step type has success, no-op, and failure tests; contract tests pin the JSON of every job and result type shared via `Rollcall.Contracts`.
5. Hybrid onboarding E2E with fakes: the run waits on `WaitFor` until FakeGraph "syncs" the user after a simulated delay, then completes the cloud steps; a sync timeout fails the step with a message pointing to Entra Connect troubleshooting.
6. Hybrid offboarding E2E with fakes: AD account disabled and in the Offboarding OU, AD groups removed and recorded, sync triggered, then mailbox and licenses handled in the M7 order.
7. Editing department on a synced user routes through the agent (not Graph) and triggers a delta sync.
8. With the agent stopped, a hybrid run shows "Waiting for AD agent" and resumes within 60 s of the agent restarting.
9. Lab domain **[manual, recorded]**: install the agent under a gMSA via the documented commands on Windows Server 2022; run a hybrid onboarding and offboarding against a test tenant with Entra Connect; confirm in ADUC and Entra.
10. Docs: `docs/day-1/06-ad-agent.md` (gMSA creation, delegation, install, verify connected), `docs/admin/ad-agent.md`, troubleshooting entries for "agent offline" and "sync delay".

---

### M10: Operations and hardening

**Goal:** Rollcall is safe to leave running for a year.

**Tasks**

- **Certificate rotation** (spec): track expiry; dashboard and email-free in-app banner from 30 days before; guided rotation: generate new cert → download/upload alongside the old one (or re-run generated script) → validate new cert works for Graph and Exchange → switch active → retire old after the admin confirms it was removed from the app registration.
- **Audit export** to CSV for any filter; "Verify audit log" action from M3 exposed in Admin.
- **Health page** (Admin › Health): DB, worker heartbeat, queue depth, failed runs in last 24 h, Graph reachability, Exchange session, permissions drift, certificate expiry, agent status, disk space of the DB volume, last backup time (written by `install.sh backup`).
- **Master key rotation** CLI: `install.sh rotate-master-key` re-wraps all data keys.
- **Data retention** setting for run payloads (default keep forever; audit log is never pruned by the app).
- **Security review:** run a threat model (STRIDE) over the design and write `docs/security/threat-model.md`; fix findings rated high; dependency scanning (Dependabot, `dotnet list package --vulnerable`, `npm audit`) in CI; container image scanning (Trivy) in CI; run as non-root in `app` and `worker`; read-only root filesystem where possible.
- **Brand assets and polish** (brand guide §14): generate the 1280x640 GitHub social preview from the SVGs; confirm raster/PWA icons and bundled fonts ship; audit every screen against `brand-guide.md` (tokens only, Lucide icon map, sentence case, voice and word list for every string, empty states, error messages) and refresh visual snapshots.

**Acceptance criteria**

1. With the injectable clock at 29 days before expiry, the banner appears for Admins only; full rotation in E2E (FakeGraph accepts both certs during overlap) ends with the new cert active and the old one `retired`; Graph and Exchange calls during rotation never fail.
2. CSV export of 10,000 audit events streams in under 10 s and opens correctly in Excel (UTF-8 BOM, ISO 8601 times, one row per event).
3. Health page shows a failing state within 60 s of stopping the worker container and of revoking a Graph permission in FakeGraph.
4. Trivy reports no critical or high vulnerabilities with a fix available in shipped images; CI fails if one appears.
5. `app` and `worker` containers run as a non-root UID (`docker compose exec app id -u` ≠ 0).
6. The threat model is merged and every high finding is linked to a merged fix or an ADR accepting the risk.
7. A brand audit checklist (tokens, icons, status icon + word, copy voice, word list, empty states, focus ring, dark mode) is completed for every route in 4.9 and attached to the PR; no lint violations of the token rule; all four §14 asset checklist items are ticked in `brand-guide.md`.
8. Docs: `docs/admin/certificate-rotation.md`, `docs/admin/backups-and-restore.md`, `docs/admin/updating.md`, `docs/admin/health.md`.

---

### M11: Documentation and v1.0 release

**Goal:** the complete documentation set from the spec, a verified day-1 guide, and a tagged v1.0.0 release.

**Tasks**

- Complete the documentation set (spec table): day-1 guide (10 steps), administrator guide, technician guide with screenshots, permissions reference, troubleshooting guide, developer and contributor guide (local dev, architecture, GPLv3 terms, coding standards, "how to add a workflow step" tutorial, release process), release notes.
- Docs site: build `docs/` with a static site generator under a GPL-compatible license (for example MkDocs with Material, MIT) styled per brand guide §12 (same tokens, self-hosted fonts, logo, and dark mode as the portal; non-affiliation line in the footer), published with GitHub Pages; the portal's Help menu links to the version-matched docs.
- Day-1 guide steps 09 (backups and a test restore) and 10 (go-live checklist).
- Release process: semantic versioning, tag `v1.0.0`, GitHub Release with notes, signed images (cosign) on GHCR, installer published at a stable URL, SBOM attached.
- **Clean-server walkthrough:** someone (human or agent) who has not worked on the code follows the day-1 guide exactly on a fresh VM against a test tenant, records every deviation, and the guide is fixed until a second run has zero deviations.

**Acceptance criteria**

1. Every document in the spec's documentation table exists under `docs/`, is linked from the docs site navigation, and is linked from the portal Help menu.
2. Each of the 10 day-1 steps has: what you're doing in one sentence, the exact command (Mono, copy button, no prompt character) or screen, the expected result, and a "Check it worked" section.
3. Screenshots follow brand guide §12: 1440x900, light mode, the fictional "Contoso" tenant, 1px border, Marigold rectangles for highlights; they are captured by a Playwright script so they can be retaken when the UI changes.
4. The clean-server walkthrough log is committed under `docs/release/v1.0.0-walkthrough.md` showing a final run with zero deviations, from empty server to a successful test onboarding and offboarding, and a successful restore.
5. `docs/reference/permissions.md` matches the generated manifest (CI check from M2 green).
6. Docs site build has zero broken links (link checker in CI).
7. `v1.0.0` release exists with notes, SBOM, and signed multi-arch images; `install.sh` from the release URL installs exactly v1.0.0.
8. All prior milestones' acceptance criteria still pass on the release commit (full CI + nightly suite green), and all **[manual]** checks from M2–M9 have been re-run against v1.0.0 and recorded in the walkthrough log.

---

## 8. Risks and spikes

| ID | Risk | Mitigation |
| --- | --- | --- |
| R1 | The `ExchangeOnlineManagement` module is Microsoft-licensed; bundling it in a published image may conflict with the "every bundled dependency GPLv3-compatible" rule | Spike S1 decides: preferred approach is to download the module from the PowerShell Gallery at first worker start (pinned version, checksum verified) so it is not redistributed in the image. Record in ADR 0002. Fallback: the documented Exchange admin REST endpoint used by the module, if Microsoft documents it for app-only use |
| R2 | Microsoft changes Graph permissions, CA APIs, or TAP behaviour | Permission manifest + health page detect drift; live test suite run nightly against the test tenant |
| R3 | Directory sync delays make hybrid onboarding slow or flaky | `WaitFor` with clear status, configurable timeout, manual "check again" button, troubleshooting doc |
| R4 | A bug in CA editing locks users out of the tenant | Only `conditions.users` is ever written, safety rails, mandatory approval, break-glass protection, report-only testing in docs |
| R5 | A compromised Rollcall host has standing admin rights | Least-privilege manifest with feature toggles, cert private key encrypted at rest, non-root containers, audit hash chain, threat model in M10, docs recommend restricting the app with CA for workload identities where licensed |
| R6 | Graph throttling during bulk actions | `$batch`, `Retry-After`, worker concurrency limits, bulk previews that warn about large changes |
| R7 | Testing against real AD in CI is impractical | `IDirectory` abstraction with fakes, contract tests, and a recorded manual lab checklist before each release |
| R8 | Brand guide changes after UI is built (colors and fonts may still change) | All visual values come from `docs/brand/tokens.css`; components never hard-code values (lint-enforced); snapshots on `/dev/components` make changes reviewable |

---

## 9. Glossary

- **Run:** one requested change, made of steps, with a frozen preview.
- **Step:** one idempotent action against Graph, Exchange, or the AD agent.
- **Plan / preview:** the read-only description of what a run will change.
- **Drift:** the target changed between preview and execution.
- **Undo data:** what a step removed, recorded so it can be restored.
- **Role template:** an Admin-defined description of what a job gets.
- **Synced user:** a user whose source of authority is on-prem AD (`onPremisesSyncEnabled = true`).
- **gMSA:** group managed service account; Windows manages its password, so no one types one in.
- **TAP:** Temporary Access Pass, a time-limited passcode for first sign-in and MFA registration.
- **Break-glass account:** an emergency admin account that must never be locked out by CA.
