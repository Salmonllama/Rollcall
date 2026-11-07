#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Renders the Compose and Caddy templates into a directory with "tls internal" and fresh
# random secrets, for testing the stack without the installer.
# Usage: render-test-stack.sh <dir> <hostname> <image-prefix> <version>
set -euo pipefail

dir="${1:?usage: render-test-stack.sh <dir> <hostname> <image-prefix> <version>}"
host="${2:?}"
prefix="${3:?}"
version="${4:?}"
templates="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

mkdir -p "$dir/tls"
sed -e "s|{{IMAGE_PREFIX}}|$prefix|g" -e "s|{{VERSION}}|$version|g" \
  "$templates/docker-compose.yml.tmpl" >"$dir/docker-compose.yml"
sed -e "s|{{HOSTNAME}}|$host|g" -e "s|{{TLS_DIRECTIVE}}|tls internal|g" \
  "$templates/Caddyfile.tmpl" >"$dir/Caddyfile"

umask 077
{
  echo "POSTGRES_PASSWORD=$(openssl rand -hex 32)"
  echo "ROLLCALL_MASTER_KEY=$(openssl rand -base64 32)"
  echo "ROLLCALL_SETUP_TOKEN=$(openssl rand 24 | base32 | tr -d '=')"
} >"$dir/.env"
