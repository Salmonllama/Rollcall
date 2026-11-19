# Architecture overview

Rollcall is a clean, layered .NET 10 + React TypeScript application built for self-hosted IT operations.

## Solution layering (enforced by unit tests)

| Project | Responsibility | Allowed project references |
| --- | --- | --- |
| `Rollcall.Contracts` | Shared DTOs, enums, API & agent wire contracts | None |
| `Rollcall.Domain` | Core entities, `IWorkflowStep`, `PlanHasher`, `AuditHashChain`, domain policies (RBAC, hybrid routing, CA safety rails, onboarding/offboarding logic, permission manifest) | `Rollcall.Contracts` |
| `Rollcall.Infrastructure` | EF Core (`RollcallDbContext` + migrations), AES-256-GCM envelope encryption, RSA 3072 certificates, Graph & Exchange Online clients, PostgreSQL job queue (`FOR UPDATE SKIP LOCKED`), workflow engine & step implementations, Welcome Pack PDF generator | `Rollcall.Domain`, `Rollcall.Contracts` |
| `Rollcall.Api` | Minimal API endpoints, single-tenant Entra OIDC + cookie + CSRF auth, OpenAPI, static SPA host, host CLI commands | `Rollcall.Infrastructure`, `Rollcall.Domain`, `Rollcall.Contracts` |
| `Rollcall.Worker` | Background job processor (`WorkflowWorkerService`), scheduler (`SchedulerBackgroundService`), heartbeat writer | `Rollcall.Infrastructure`, `Rollcall.Domain`, `Rollcall.Contracts` |
| `Rollcall.Agent` | Outbound-only Windows Service for hybrid on-premises Active Directory (`LdapActiveDirectory`, `OuScopeGuard`, ECDSA P-256 JWT auth) | `Rollcall.Contracts` |
| `web/` | React 19 + TypeScript + Vite SPA styled exclusively from `docs/brand/tokens.css` | Consumes `/api/*` |

## Workflow lifecycle

1. **Plan:** Each `IWorkflowStep.PlanAsync` reads live directory state and returns a `StepPlan` (before/after values, warnings, blocking errors, no-op flag).
2. **Freeze:** `WorkflowEngine` serializes the plan and computes a canonical SHA-256 `plan_hash`.
3. **Confirm / Approve:** User confirms matching `plan_hash` (and typed confirmation for destructive/CA actions). If policy requires approval, the run enters `AwaitingApproval`.
4. **Execute:** `Rollcall.Worker` leases a job from PostgreSQL (`FOR UPDATE SKIP LOCKED`), checks live state against the frozen plan for drift (`NeedsReview` if drifted), executes each step in order, and appends hash-chained audit events.
5. **Retry / Restore:** Failed runs resume strictly from the first failed step; completed offboardings can be reversed from stored `UndoDescriptor` metadata.
