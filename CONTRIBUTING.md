# Contributing to Rollcall

Thanks for helping. Rollcall manages identities in real Microsoft 365 tenants, so we value careful, reviewable changes over fast ones. This guide covers how to build and test, the rules every pull request follows, and how to sign off your work.

## Before you start

- Read the [product spec](docs/product-spec.md) for *what* to build, the [implementation plan](docs/implementation-plan.md) for *how*, and the [brand guide](docs/brand-guide.md) for anything visual or any user-facing text.
- Work follows the milestones in the implementation plan, in order. Open an issue before starting anything large so we can agree where it fits.
- Follow the [code of conduct](CODE_OF_CONDUCT.md). Report security problems privately as described in [SECURITY.md](SECURITY.md).

## Build and test

You need the .NET 10 SDK, Node.js 22 or later, and Docker. The .NET integration tests start a throwaway PostgreSQL 17 container with [Testcontainers](https://dotnet.testcontainers.org/).

```bash
dotnet build src/Rollcall.sln
dotnet test src/Rollcall.sln
dotnet format src/Rollcall.sln --verify-no-changes
```

```bash
cd web
npm ci
npm run lint
npm run format:check
npm run typecheck
npm test
npm run build
```

Checks that CI also runs:

```bash
scripts/check-spdx-headers.sh
scripts/check-nuget-licenses.sh
node scripts/check-npm-licenses.mjs
shellcheck scripts/*.sh
```

### Database migrations

The schema lives in `src/Rollcall.Infrastructure/Persistence/Migrations/`. To add a migration:

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add <Name> --project src/Rollcall.Infrastructure --output-dir Persistence/Migrations
```

Add the SPDX header to the top of each generated file. The app applies pending migrations on startup, holding a PostgreSQL advisory lock so two app containers never migrate at once.

## Rules for every pull request

- **One milestone at a time.** A milestone may span several pull requests, but the next one does not start until every acceptance criterion of the current one passes.
- **Small and reviewable.** Aim for under about 800 changed lines, excluding generated code, lockfiles, and snapshots. Title pull requests `M<n>: <what>`.
- **Acceptance criteria are the contract.** Show each criterion with an automated test, a CI job, or, only where automation is impossible, a written check in the pull request description marked **[manual]**.
- **Never touch a production tenant.** Live Microsoft 365 tests use a dedicated test tenant, are opt-in, and read credentials from environment variables, never from committed files.
- **No secrets, ever.** Never add a client secret code path, a stored admin password, or anything that collects a user's Microsoft 365 or AD password.
- **Docs ship with code.** If a change alters behavior a user or admin can see, update the matching page under `docs/` in the same pull request, and add an entry under `Unreleased` in [CHANGELOG.md](CHANGELOG.md).
- **Record decisions.** Any choice the implementation plan does not settle goes in an architecture decision record under [`docs/adr/`](docs/adr/) named `NNNN-title.md`, with Context, Decision, and Consequences sections.
- **Licensing.** Every dependency and base image must have a GPLv3-compatible license. The allowed list is [`.github/licenses/policy.json`](.github/licenses/policy.json) and CI enforces it. If something you need fails the check, stop and write an ADR.

## Coding standards

- **License header.** Every source file starts with `SPDX-License-Identifier: GPL-3.0-or-later` as a comment. The C# build and CI both fail without it.
- **C#.** Nullable reference types on, warnings are errors, the `latest-recommended` analyzer set, file-scoped namespaces. Run `dotnet format` before you push. Test names read as sentences: `Rejecting_without_a_comment_returns_400`.
- **TypeScript.** `strict` mode, ESLint with the type-checked rules, Prettier for formatting. Components use only the `var(--rc-*)` tokens from [`docs/brand/tokens.css`](docs/brand/tokens.css).
- **Shell.** Bash, `set -euo pipefail`, and `shellcheck` clean.
- **User-facing text** follows the voice and word list in brand guide section 11: sentence case, active voice, name the object and count, and errors that say what happened and what to do next.

## Sign your work (DCO)

Rollcall uses the [Developer Certificate of Origin](https://developercertificate.org/) instead of a contributor license agreement. By signing off a commit you certify that you wrote it or otherwise have the right to submit it under the project's license.

Add a sign-off line to every commit with `git commit -s`:

```text
Signed-off-by: Your Name <you@example.com>
```

The name and email must match the commit author. CI checks every commit in a pull request and fails if a sign-off is missing. To fix the last commit, run `git commit --amend -s`; for several commits, run `git rebase --signoff main`.

## License

By contributing, you agree that your contributions are licensed under the [GNU General Public License v3.0 or later](LICENSE).
