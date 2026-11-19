#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Starts Rollcall API, Worker, and Vite dev server locally against a Postgres database.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

export ConnectionStrings__Rollcall="${ConnectionStrings__Rollcall:-Host=127.0.0.1;Port=5432;Database=rollcall_dev;Username=postgres;Password=postgres}"
export ROLLCALL_MASTER_KEY="${ROLLCALL_MASTER_KEY:-MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=}"
export ROLLCALL_SETUP_TOKEN="${ROLLCALL_SETUP_TOKEN:-DEVSETUPTOKEN1234567890ABCDEF}"
export ROLLCALL_HOSTNAME="${ROLLCALL_HOSTNAME:-localhost:5173}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

echo "Starting Rollcall local development stack..."
echo "  API:      http://localhost:5080"
echo "  Web UI:   http://localhost:5173 (dev components at /dev/components)"
echo "  Token:    $ROLLCALL_SETUP_TOKEN"

cleanup() {
  jobs -p | xargs -r kill 2>/dev/null || true
}
trap cleanup EXIT INT TERM

(cd "$ROOT_DIR" && dotnet run --project src/Rollcall.Api --urls http://127.0.0.1:5080) &
(cd "$ROOT_DIR" && dotnet run --project src/Rollcall.Worker) &
(cd "$ROOT_DIR/web" && npm run dev) &

wait
