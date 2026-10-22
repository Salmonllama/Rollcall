#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Fails when a source file lacks the SPDX header in its first five lines, or LICENSE is not the unmodified GPLv3 text.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

header='SPDX-License-Identifier: GPL-3.0-or-later'
# SHA-256 of https://www.gnu.org/licenses/gpl-3.0.txt
gpl3_sha256='3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986'

status=0

if [[ "$(sha256sum LICENSE | cut -d' ' -f1)" != "$gpl3_sha256" ]]; then
  echo "✕ LICENSE is not the unmodified GPLv3 text. Restore it from https://www.gnu.org/licenses/gpl-3.0.txt" >&2
  status=1
fi

missing=()
while IFS= read -r -d '' file; do
  [[ -f "$file" ]] || continue
  if ! head -n 5 "$file" | grep -qF "$header"; then
    missing+=("$file")
  fi
done < <(git ls-files -z --cached --others --exclude-standard -- \
  '*.cs' '*.csproj' '*.props' '*.targets' '*.ts' '*.tsx' '*.js' '*.mjs' '*.cjs' '*.sh' '*.bats' '*.ps1' \
  'web/src/*.css' 'web/index.html' '*Dockerfile' ':!:docs/**')

if ((${#missing[@]} > 0)); then
  echo "✕ ${#missing[@]} file(s) are missing the header \"$header\" in their first five lines:" >&2
  printf '  %s\n' "${missing[@]}" >&2
  echo "Add the header as a comment at the top of each file." >&2
  status=1
fi

if ((status == 0)); then
  echo "✓ LICENSE is GPLv3 and every source file carries the SPDX header."
fi
exit "$status"
