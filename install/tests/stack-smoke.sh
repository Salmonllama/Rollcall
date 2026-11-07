#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Checks a running Rollcall stack against M1 acceptance criteria 2, 3, and 5.
# Usage: stack-smoke.sh <install-dir> <hostname>
# The hostname is resolved to 127.0.0.1, so no DNS entry is needed. With "tls internal",
# the check trusts Caddy's local root certificate; otherwise it uses the system trust store
# unless CURL_CA_BUNDLE is set.
set -euo pipefail

dir="${1:?usage: stack-smoke.sh <install-dir> <hostname>}"
host="${2:?usage: stack-smoke.sh <install-dir> <hostname>}"
cd "$dir"

failures=0
pass() { printf '✓ %s\n' "$1"; }
fail() { printf '✕ %s\n' "$1" >&2; failures=$((failures + 1)); }

curl_args=(--silent --show-error --max-time 10 --noproxy '*' --resolve "$host:443:127.0.0.1" --resolve "$host:80:127.0.0.1")
if grep -q 'tls internal' Caddyfile; then
  ca="$(mktemp)"
  trap 'rm -f "$ca"' EXIT
  docker compose cp proxy:/data/caddy/pki/authorities/local/root.crt "$ca" >/dev/null 2>&1
  curl_args+=(--cacert "$ca")
fi

# Criterion 2: exactly four services, all healthy.
mapfile -t services < <(docker compose ps --all --format '{{.Service}} {{.Health}}' | sort)
if [[ ${#services[@]} -eq 4 ]]; then
  pass "Exactly four services are running"
else
  fail "Expected 4 services, found ${#services[@]}: ${services[*]}"
fi
for entry in "${services[@]}"; do
  if [[ "${entry#* }" == healthy ]]; then
    pass "${entry% *} is healthy"
  else
    fail "${entry% *} is not healthy (${entry#* })"
  fi
done

ready="$(curl "${curl_args[@]}" --write-out '\n%{http_code}' "https://$host/readyz" || true)"
expected='{"db":"ok","migrations":"current","worker":"ok"}'
if [[ "$ready" == "$expected"$'\n'200 ]]; then
  pass "GET https://$host/readyz returns 200 with $expected"
else
  fail "GET https://$host/readyz returned: ${ready//$'\n'/ (HTTP }"
fi

# Criterion 3: HTTP redirects to HTTPS; HSTS and CSP headers are set.
redirect="$(curl "${curl_args[@]}" --output /dev/null --write-out '%{http_code} %{redirect_url}' "http://$host/" || true)"
if [[ "$redirect" =~ ^30[178]\ https://$host/ ]]; then
  pass "HTTP redirects to HTTPS"
else
  fail "HTTP did not redirect to HTTPS (got: $redirect)"
fi

headers="$(curl "${curl_args[@]}" --head "https://$host/" || true)"
for header in 'strict-transport-security: max-age=31536000' "content-security-policy: default-src 'self'" \
  'x-content-type-options: nosniff' 'referrer-policy: same-origin'; do
  if grep -qiF "$header" <<<"$headers"; then
    pass "Response has ${header%%:*}"
  else
    fail "Response is missing \"$header\""
  fi
done
if grep -qiF "frame-ancestors 'none'" <<<"$headers"; then
  pass "CSP sets frame-ancestors 'none'"
else
  fail "CSP is missing frame-ancestors 'none'"
fi

# Criterion 5: the database publishes no host ports. Recent Compose versions print ":0"
# for an unpublished port instead of nothing.
db_port="$(docker compose port db 5432 2>/dev/null || true)"
if [[ -z "$db_port" || "$db_port" == ":0" ]]; then
  pass "The db service publishes no host ports"
else
  fail "The db service publishes port 5432 on the host"
fi

if ((failures > 0)); then
  printf '✕ %d check(s) failed.\n' "$failures" >&2
  exit 1
fi
printf '✓ All stack checks passed.\n'
