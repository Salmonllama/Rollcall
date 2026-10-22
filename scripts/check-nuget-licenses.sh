#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Fails when any NuGet package (direct or transitive) has a license outside .github/licenses/policy.json.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
policy="$root/.github/licenses/policy.json"

allowed="$(python3 -c 'import json,sys; print(";".join(json.load(open(sys.argv[1]))["allowed"]))' "$policy")"
ignored="$(python3 -c 'import json,sys; print(";".join(json.load(open(sys.argv[1]))["exceptions"]["nuget"]))' "$policy")"

args=(-i "$root/src/Rollcall.sln" -t -a "$allowed" -mapping "$root/.github/licenses/nuget-url-mappings.json" -o Markdown)
if [[ -n "$ignored" ]]; then
  args+=(-ignore "$ignored")
fi

cd "$root"
dotnet tool restore >/dev/null
if ! output="$(dotnet nuget-license "${args[@]}" 2>&1)"; then
  # Show only rows with an error: package, version, license, error.
  printf '%s\n' "$output" | awk -F'|' 'NR > 2 && $10 ~ /[^ ]/ { gsub(/ +/, " "); print "  " $2 $3 $5 "|" $10 }'
  echo "✕ NuGet license check failed. Remove the package, or record an ADR and update .github/licenses/policy.json." >&2
  exit 1
fi
count="$(printf '%s\n' "$output" | grep -c '^| [A-Za-z]' || true)"
echo "✓ $((count - 1)) NuGet packages checked; all licenses are GPLv3-compatible."
