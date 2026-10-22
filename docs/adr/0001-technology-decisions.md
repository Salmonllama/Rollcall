# 0001: Technology decisions for v1

- Status: Accepted
- Date: Oct 22, 2026

## Context

Rollcall is a self-hosted Microsoft 365 onboarding and offboarding portal released under GPLv3 ([product spec](../product-spec.md)). Before writing features we need one recorded set of technology choices so that every milestone builds on the same foundations, and so that later changes are deliberate and reviewed. Section 2 of the [implementation plan](../implementation-plan.md#2-technology-decisions) proposes these choices; the maintainer confirmed the .NET 10 and React stack over TypeScript/Node and Python alternatives on Oct 21, 2026.

## Decision

We adopt the stack in implementation plan section 2 for v1:

| Concern | Choice |
| --- | --- |
| Backend | C# on .NET 10 (LTS); ASP.NET Core minimal APIs with a generated OpenAPI document |
| Job worker and AD agent | .NET 10 Worker Service; the agent runs as a Windows service under a gMSA |
| Frontend | React 19, TypeScript in `strict` mode, Vite, React Router, TanStack Query |
| UI | Radix UI primitives styled only with `docs/brand/tokens.css`; Lucide icons; self-hosted Atkinson Hyperlegible Next and Mono |
| Database | PostgreSQL 17 through EF Core and Npgsql, code-first migrations |
| Job queue | PostgreSQL table with `SELECT … FOR UPDATE SKIP LOCKED` |
| Microsoft Graph | `Microsoft.Graph` SDK, app-only auth with a client certificate (`Azure.Identity`) |
| Exchange Online | `ExchangeOnlineManagement` PowerShell module hosted in the worker; how the module is obtained is decided in [ADR 0002](0002-exchange-online-and-certificates.md) |
| Technician sign-in | OIDC authorization code flow with PKCE (`Microsoft.Identity.Web`), certificate client assertion |
| Reverse proxy | Caddy 2 |
| Installer | Bash, `shellcheck` clean |
| Containers and CI | Multi-arch images on GHCR, GitHub Actions |
| Logging | `Microsoft.Extensions.Logging` with JSON console output |

M0 also settles these repository conventions:

- **Solution layout** follows implementation plan section 3. `src/Rollcall.sln` uses the classic `.sln` format (the .NET 10 default is `.slnx`) because the plan and tooling such as `nuget-license` reference it by that name. `Rollcall.Agent` references only `Rollcall.Contracts`, and `Rollcall.Domain` references only `Rollcall.Contracts`; tests enforce both.
- **Build settings** live in `Directory.Build.props`: nullable enabled, warnings as errors, the `latest-recommended` analyzer set, and code style enforced at build time. Package versions are pinned centrally in `Directory.Packages.props`.
- **License headers.** Every source file starts with `SPDX-License-Identifier: GPL-3.0-or-later`. For C# the `IDE0073` rule fails the build; `scripts/check-spdx-headers.sh` checks every language and the LICENSE text in CI.
- **License compliance.** `.github/licenses/policy.json` is the single list of GPLv3-compatible licenses. CI checks every NuGet package (direct and transitive, with `nuget-license`) and every npm package (with `license-checker-rseidelsohn`). Exceptions are allowed only for development-only npm packages, each with a written reason; a bundled package is never exempt.
- **Web linting** uses ESLint with the type-checked `typescript-eslint` rules and Prettier, as the plan specifies, rather than the `oxlint` setup the current Vite template generates.
- **Test assertions** use xUnit's built-in `Assert`. FluentAssertions 8 and later is under a commercial license that is not GPLv3-compatible, so it is not used; if fluent assertions become worth adding, use an Apache-2.0 option such as FluentAssertions 7.x or AwesomeAssertions after a license check.
- **Contributions** use the Developer Certificate of Origin; CI checks every pull request commit for a matching `Signed-off-by` line.
- **Containers** run as the non-root `app` user that the .NET base images provide.

## Consequences

- Contributors need the .NET 10 SDK and Node.js 22 to build everything; CONTRIBUTING.md lists the commands.
- Any change to a row above, or adding a dependency whose license is not on the allow list, needs a new ADR.
- Branch protection on `main` must require the `CI passed` check so that a failed build, test, lint, license, or header job blocks merging. This is a repository setting, not code, and a maintainer applies it.
