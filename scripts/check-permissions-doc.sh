#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Verifies docs/reference/permissions.md matches the live PermissionManifestBuilder output.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TMP_OUT="$(mktemp)"
trap 'rm -f "$TMP_OUT"' EXIT

dotnet run --no-launch-profile --project "$ROOT_DIR/src/Rollcall.Api/Rollcall.Api.csproj" -c Release --no-build -- \
  generate-permissions-doc "$TMP_OUT" >/dev/null

if ! diff -u "$ROOT_DIR/docs/reference/permissions.md" "$TMP_OUT"; then
  echo "✕ docs/reference/permissions.md is out of date." >&2
  echo "  Run: dotnet run --project src/Rollcall.Api -- generate-permissions-doc docs/reference/permissions.md" >&2
  exit 1
fi

echo "✓ docs/reference/permissions.md is up to date."
