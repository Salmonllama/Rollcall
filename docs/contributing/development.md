# Local development guide

## Prerequisites

- .NET 10 SDK (pinned in `global.json`)
- Node.js 22+ and npm
- PostgreSQL 15+ or Docker (for Testcontainers in integration tests)
- `shellcheck` and `bats` (optional, for testing `install/install.sh`)

## Quick start

```bash
./dev.sh
```

Or run services manually:

```bash
# 1. Start PostgreSQL (or use your local instance) and set connection string:
export ConnectionStrings__Rollcall="Host=127.0.0.1;Port=5432;Database=rollcall_dev;Username=postgres;Password=postgres"
export ROLLCALL_MASTER_KEY="$(head -c 32 /dev/urandom | base64 -w0)"
export ROLLCALL_SETUP_TOKEN="DEVSETUPTOKEN1234567890"
export ROLLCALL_HOSTNAME="localhost:5173"

# 2. Run API, Worker, and Web dev server in separate terminals:
dotnet run --project src/Rollcall.Api
dotnet run --project src/Rollcall.Worker
cd web && npm install && npm run dev
```

In development mode, the component reference page is available at `/dev/components`.

## Running tests & checks

```bash
dotnet build src/Rollcall.sln -c Release
dotnet test src/Rollcall.sln -c Release
dotnet format src/Rollcall.sln --verify-no-changes

cd web && npm run lint && npm run format:check && npm run typecheck && npm test && npm run build

scripts/check-spdx-headers.sh
scripts/check-nuget-licenses.sh
node scripts/check-npm-licenses.mjs
scripts/check-permissions-doc.sh
```

## Contributing rules

- Every source file must start with `SPDX-License-Identifier: GPL-3.0-or-later`.
- Every commit must carry a DCO sign-off (`git commit -s`).
- Every NuGet and npm dependency must be GPLv3-compatible (verified against `.github/licenses/policy.json`).
- All CSS in `web/src/` must use `var(--rc-*)` tokens from `docs/brand/tokens.css` (enforced by `web/scripts/lint-tokens.mjs`).
