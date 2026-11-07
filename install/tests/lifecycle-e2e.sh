#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# End-to-end checks for M1 acceptance criteria 6 (backup, purge, reinstall, restore) and
# 7 (update, and rollback after a failed migration), against an installed stack.
#
# Usage: lifecycle-e2e.sh backup-restore <hostname>
#        lifecycle-e2e.sh update <hostname> <good-version> <broken-version>
#
# The installer must already have installed version "edge". For "update", the images for
# <good-version> and <broken-version> must be pullable from $ROLLCALL_IMAGE_PREFIX; the
# broken app image carries a migration that always fails.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
installer="$here/../install.sh"
dir="${ROLLCALL_DIR:-/opt/rollcall}"
mode="${1:?usage: lifecycle-e2e.sh backup-restore|update <hostname> ...}"
host="${2:?hostname required}"

pass() { printf '✓ %s\n' "$1"; }
die() { printf '✕ %s\n' "$1" >&2; exit 1; }

compose() { docker compose --project-directory "$dir" -f "$dir/docker-compose.yml" "$@"; }
psql_db() { compose exec -T db psql -U rollcall -d rollcall -tAc "$1"; }
seed_checksum() { psql_db "SELECT count(*) || ':' || md5(string_agg(id || val, ',' ORDER BY id)) FROM ci_seed"; }
env_value() { sed -n "s/^$1=//p" "$dir/.env"; }

seed() {
  psql_db "CREATE TABLE ci_seed AS SELECT g AS id, md5(g::text) AS val FROM generate_series(1, 1000) g" >/dev/null
  seed_checksum
}

backup_restore() {
  local before archive after password_before
  before="$(seed)"
  password_before="$(env_value POSTGRES_PASSWORD)"
  pass "Seeded ci_seed ($before)"

  archive="$(bash "$installer" backup | tee /dev/stderr | sed -n 's/^✓ Backup written to \(.*\)\.$/\1/p')"
  [[ -f "$archive" ]] || die "backup didn't report an archive path"
  [[ "$(stat -c %a "$archive")" == 600 ]] || die "backup archive isn't mode 600"
  pass "Backup archive $archive is mode 600"

  bash "$installer" uninstall --purge --yes
  [[ ! -e "$dir" ]] || die "$dir still exists after uninstall --purge"
  ! docker volume inspect rollcall_db-data >/dev/null 2>&1 || die "the db-data volume survived uninstall --purge"
  pass "uninstall --purge removed $dir and the volumes"

  bash "$installer" install --hostname "$host" --tls internal --yes >/dev/null
  [[ "$(env_value POSTGRES_PASSWORD)" != "$password_before" ]] || die "the fresh install reused the old secrets"
  ! psql_db "SELECT 1 FROM ci_seed" >/dev/null 2>&1 || die "the fresh install still has ci_seed"
  pass "Fresh install has new secrets and no seeded data"

  bash "$installer" restore "$archive" --yes
  after="$(seed_checksum)"
  [[ "$after" == "$before" ]] || die "restored ci_seed is $after, expected $before"
  [[ "$(env_value POSTGRES_PASSWORD)" == "$password_before" ]] || die "restore didn't bring back the original .env"
  pass "Restored ci_seed matches: $after"
}

update() {
  local good="${3:?good version required}" broken="${4:?broken version required}" before out status
  local work
  work="$(mktemp -d)"
  trap 'rm -rf "$work"' RETURN
  psql_db "SELECT 1 FROM ci_seed" >/dev/null 2>&1 || seed >/dev/null
  before="$(seed_checksum)"

  # An installer for each target version, as "update" would download it.
  for version in "$broken" "$good"; do
    mkdir -p "$work/$version"
    cp "$here"/../install.sh "$here"/../*.tmpl "$work/$version/"
    sed -i "s/^ROLLCALL_VERSION=\".*\"$/ROLLCALL_VERSION=\"$version\"/" "$work/$version/install.sh"
  done

  set +e
  out="$(ROLLCALL_UPDATE_SOURCE=local bash "$work/$broken/install.sh" update --yes 2>&1)"
  status=$?
  set -e
  printf '%s\n' "$out"
  [[ $status -ne 0 ]] || die "update to the broken version exited 0"
  [[ "$out" == *"The backup taken before the update is /"* ]] || die "the failed update didn't print the backup path"
  [[ "$(env_value ROLLCALL_VERSION)" == edge ]] || die "ROLLCALL_VERSION is $(env_value ROLLCALL_VERSION) after the rollback"
  grep -q 'rollcall-app:edge$' "$dir/docker-compose.yml" || die "docker-compose.yml doesn't point at edge after the rollback"
  [[ "$(seed_checksum)" == "$before" ]] || die "data changed after the rollback"
  ! psql_db "SELECT 1 FROM ci_half_applied" >/dev/null 2>&1 || die "the broken migration's table survived the rollback"
  "$here/stack-smoke.sh" "$dir" "$host" >/dev/null || die "the stack isn't healthy after the rollback"
  pass "A failed migration left edge running with its data and printed the backup path"

  ROLLCALL_UPDATE_SOURCE=local bash "$work/$good/install.sh" update --yes
  [[ "$(env_value ROLLCALL_VERSION)" == "$good" ]] || die "ROLLCALL_VERSION is $(env_value ROLLCALL_VERSION) after the update"
  [[ "$(seed_checksum)" == "$before" ]] || die "data changed during the update"
  "$here/stack-smoke.sh" "$dir" "$host" >/dev/null || die "the stack isn't healthy after the update"
  pass "Update from edge to $good finished and the stack is healthy"
}

case "$mode" in
  backup-restore) backup_restore ;;
  update) update "$@" ;;
  *) die "unknown mode $mode" ;;
esac
