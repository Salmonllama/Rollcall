#!/usr/bin/env node
// SPDX-License-Identifier: GPL-3.0-or-later
// Fails when any npm package in web/ has a license outside .github/licenses/policy.json.
// Exceptions apply to development-only packages; anything bundled into the SPA must be on the allow list.
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isAllowed } from './spdx.mjs';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const policy = JSON.parse(readFileSync(join(root, '.github/licenses/policy.json'), 'utf8'));
const allowed = new Set(policy.allowed);
const exceptions = policy.exceptions.npm;

function scan(productionOnly) {
  const args = ['license-checker-rseidelsohn', '--json', '--excludePrivatePackages'];
  if (productionOnly) args.push('--production');
  const out = execFileSync('npx', ['--no-install', ...args], {
    cwd: join(root, 'web'),
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
  return Object.entries(JSON.parse(out)).map(([id, info]) => ({
    id,
    name: id.slice(0, id.lastIndexOf('@')),
    license: Array.isArray(info.licenses) ? info.licenses.join(' OR ') : String(info.licenses),
  }));
}

const production = new Set(scan(true).map((p) => p.id));
const failures = [];
const all = scan(false);
for (const pkg of all) {
  if (isAllowed(pkg.license, allowed)) continue;
  if (exceptions[pkg.name] && !production.has(pkg.id)) continue;
  failures.push(`${pkg.id}: ${pkg.license}${production.has(pkg.id) ? ' (bundled)' : ''}`);
}

if (failures.length > 0) {
  console.error(`✕ ${failures.length} npm package(s) have a license that is not GPLv3-compatible:`);
  for (const f of failures) console.error(`  ${f}`);
  console.error('Remove the package, or record an ADR and update .github/licenses/policy.json.');
  process.exit(1);
}
console.log(`✓ ${all.length} npm packages checked; all licenses are GPLv3-compatible.`);
