# Rollcall STRIDE threat model (v1.0)

This document analyzes the security boundaries, trust assumptions, threats, and implemented mitigations for Rollcall v1.0.

## System boundaries & assets

| Asset | Storage / transport | Protection |
| --- | --- | --- |
| Entra app RSA 3072 private key | Encrypted in PostgreSQL (`app_certificate.encrypted_private_key`) | AES-256-GCM envelope encryption wrapped by `ROLLCALL_MASTER_KEY` (`/opt/rollcall/.env`, `0600 root:root`); loaded in memory with `EphemeralKeySet` and never written to disk |
| Portal session cookie | `HttpOnly`, `Secure`, `SameSite=Lax` cookie | Encrypted ASP.NET Core cookie authentication, 8-hour absolute / 1-hour idle timeout, server-side role re-check on every request |
| Temporary Access Pass / temporary passwords | In-memory `EphemeralCredentialStore` only | Held in RAM with 15-minute TTL, single-view (`410 Gone` after first retrieval), never persisted to PostgreSQL, logs, or audit events |
| Audit log (`audit_event`) | PostgreSQL append-only table | SHA-256 canonical hash chain (`prev_hash` -> `hash`), PostgreSQL `BEFORE UPDATE OR DELETE` trigger blocking modification, verifiable via `GET /api/audit/verify` |
| AD agent private key | Windows host (`ProgramData\RollcallAgent\agent-key.bin`) protected by DPAPI `LocalMachine` | ECDSA P-256 asymmetric keypair; public key registered on enrollment; short-lived 5-minute JWTs with `jti` replay protection |
| PostgreSQL database | Docker `backend` internal network only | No published host port; reachable only by `app` and `worker` containers |

## STRIDE analysis

| Category | Threat | Impact | Mitigation in v1.0 | Residual risk / operator responsibility |
| --- | --- | --- | --- | --- |
| **Spoofing** | Attacker presents an Entra token from another tenant or forged OIDC token | Unauthorized portal access | Single-tenant issuer validation (`https://login.microsoftonline.com/<tenantId>/v2.0`), OIDC authorization code + PKCE + RSA client assertion, invite-only allowlist (`portal_user`), no local password auth | Admins must protect Entra user accounts with MFA / Conditional Access |
| **Spoofing** | Rogue host impersonates an enrolled AD agent | Execution of unauthorized or forged job results | Enrollment requires a single-use Admin-issued token (15-min TTL) consumed in a DB transaction; runtime calls require ECDSA P-256 JWT signed by agent's enrolled private key with 5-minute `exp` and `jti` check; Admins can revoke any agent immediately | Protect the Windows host where the agent runs as a Tier-0/Tier-1 administrative server |
| **Tampering** | Technician modifies plan between preview and execution, or external change happens before execution | Unexpected directory mutations | Immutable frozen plan JSON + SHA-256 `plan_hash` verified at confirmation; worker runs drift check before mutating and pauses run in `NeedsReview` if live directory differs from plan | Approvers should review the frozen diff before approving |
| **Tampering** | Compromised app user or DB operator alters historical audit records | Loss of forensic integrity | PostgreSQL trigger blocks `UPDATE`/`DELETE` on `audit_event`; every row is SHA-256 hash-chained; `/api/audit/verify` detects tampering immediately; CSV export supports archival to external WORM storage | A host root/PostgreSQL superuser can drop triggers; ship periodic backups and audit CSVs off-host |
| **Tampering** | Conditional Access edit accidentally modifies grant controls, app scope, or locks out break-glass accounts | Tenant-wide lockout | `PATCH` payload restricted strictly to `conditions.users`; hard block if a break-glass account would be removed from exclusions; policy-wide lockout check; mandatory approval + typed confirmation; Admin-only route | Admins must register all break-glass accounts in **Admin › Conditional Access** |
| **Tampering** | Supply-chain compromise of Exchange Online PowerShell module (ADR 0002) | Arbitrary code execution in worker | `ExchangeOnlineManagement` 3.10.1 fetched at runtime from PSGallery and verified against pinned SHA-256 module tree hash before `Import-Module` | Pin updates require explicit PR review and hash recalculation |
| **Repudiation** | Technician or approver denies performing a directory action | Audit failure | Every sign-in, plan, approval, rejection, step execution, credential view, setting change, cert rotation, and host-shell `admin-reset`/`rotate-master-key` writes an actor-attributed `audit_event` with correlation ID | Keep host clock synchronized (NTP) |
| **Information disclosure** | Secrets, TAPs, passwords, or private keys leak into logs, DB, or HTTP responses | Account takeover or credential theft | `SecretRedactor` scrubs bearer tokens, PEM blocks, TAPs, and passwords; TAP/temp passwords live only in `EphemeralCredentialStore` (RAM); no client secrets exist anywhere in code; `.env` is `0600 root:root`; backups are `0600` | Store backup archives encrypted/restricted off-host because they contain `ROLLCALL_MASTER_KEY` |
| **Information disclosure** | CSRF or XSS steals session or triggers state-changing actions | Unauthorized directory changes | Strict CSP (`default-src 'self'`), `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, HSTS via Caddy, `HttpOnly`/`Secure`/`SameSite=Lax` cookies, double-submit CSRF token (`X-CSRF-TOKEN`) required on all unsafe API routes | Keep browser and Rollcall updated |
| **Denial of service** | Bulk operation or runaway job floods Microsoft Graph or locks out concurrent edits | Throttling (429) or partial corruption | Per-target-user job serialization; Graph `$batch` (20/request); `Retry-After` backoff; worker concurrency cap; graceful shutdown lease release (`FOR UPDATE SKIP LOCKED`) | Size host RAM/CPU per prerequisites (2 vCPU / 4 GB RAM minimum) |
| **Elevation of privilege** | Technician calls Admin/Approver API directly, approves own request, or modifies AD objects outside allowed OUs | Unauthorized privilege escalation | Server-side role checks on every endpoint (UI hiding is cosmetic only); self-approval blocked (`requested_by_upn != approver_upn`) unless explicitly allowed by Admin policy; `OuScopeGuard` enforces allowed OU allowlist on both API and AD agent before every LDAP write; containers run non-root with `no-new-privileges:true` | Never grant the AD agent's gMSA rights over Domain Admins, Enterprise Admins, or Tier-0 OUs |

## Security verification controls in CI

- Static analysis: `.NET` `TreatWarningsAsErrors=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, `Nullable=enable`.
- Dependency vulnerability audits: `dotnet list package --vulnerable` and `npm audit --audit-level=high`.
- License & SPDX verification: 100% GPLv3-compatible dependency allowlist and SPDX header enforcement.
- Container hardening: non-root runtime UID (`$APP_UID`), `no-new-privileges:true`, internal-only Postgres network, zero bundled secrets.
