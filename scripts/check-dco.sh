#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Usage: check-dco.sh <base-sha> <head-sha>
# Fails when a non-merge commit in base..head lacks a "Signed-off-by:" line matching its author.
set -euo pipefail

base="$1"
head="$2"
missing=0

while IFS= read -r sha; do
  author="$(git log -1 --format='%an <%ae>' "$sha")"
  if ! git log -1 --format='%(trailers:key=Signed-off-by,valueonly)' "$sha" | grep -qxF "$author"; then
    echo "✕ $(git log -1 --format='%h %s' "$sha") is not signed off by $author"
    missing=1
  fi
done < <(git rev-list --no-merges "$base..$head")

if ((missing)); then
  echo "Sign off with 'git commit --amend -s' or 'git rebase --signoff $base', then push again. See CONTRIBUTING.md."
  exit 1
fi
echo "✓ Every commit is signed off by its author."
