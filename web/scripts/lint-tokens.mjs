#!/usr/bin/env node
// SPDX-License-Identifier: GPL-3.0-or-later
// Enforces brand-guide.md & implementation-plan.md section 4.9 CSS token rules:
// - No raw hex, rgb/rgba, or hsl/hsla colors outside docs/brand/tokens.css
// - No raw px font-size values (must use var(--rc-text-*))
// - No raw box-shadow literals (must use none or var(--rc-shadow-overlay))
// - Never Microsoft brand blue (#0078D4) anywhere in web/

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, extname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const srcDir = join(webRoot, 'src');

export function lintCssContent(content, fileLabel) {
  const violations = [];
  // Strip CSS block comments before scanning
  const stripped = content.replace(/\/\*[\s\S]*?\*\//g, (m) => m.replace(/[^\n]/g, ' '));
  const lines = stripped.split('\n');

  lines.forEach((line, idx) => {
    const lineNo = idx + 1;
    if (/#0078d4/i.test(line)) {
      violations.push(`${fileLabel}:${lineNo}: Microsoft brand blue (#0078D4) is forbidden.`);
    }
    if (/#[0-9a-f]{3,8}\b/i.test(line)) {
      violations.push(
        `${fileLabel}:${lineNo}: Hard-coded hex color found; use var(--rc-*) from tokens.css.`,
      );
    }
    if (/\b(?:rgba?|hsla?)\s*\(/i.test(line)) {
      violations.push(
        `${fileLabel}:${lineNo}: Hard-coded rgb/hsl color found; use var(--rc-*) from tokens.css.`,
      );
    }
    if (/font-size\s*:\s*[^;]*\d+px/i.test(line)) {
      violations.push(`${fileLabel}:${lineNo}: Raw px font-size found; use var(--rc-text-*).`);
    }
    if (
      /box-shadow\s*:\s*(?!\s*(?:none|inherit|initial|var\(--rc-shadow-overlay\))\s*;)/i.test(line)
    ) {
      violations.push(
        `${fileLabel}:${lineNo}: Raw box-shadow literal found; use none or var(--rc-shadow-overlay).`,
      );
    }
  });

  return violations;
}

function walk(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    const st = statSync(full);
    if (st.isDirectory()) {
      walk(full, out);
    } else if (extname(full) === '.css') {
      out.push(full);
    }
  }
  return out;
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
  const cssFiles = walk(srcDir);
  const allViolations = [];
  for (const file of cssFiles) {
    const content = readFileSync(file, 'utf8');
    allViolations.push(...lintCssContent(content, relative(webRoot, file)));
  }

  // Also ensure #0078D4 never appears in TS/TSX files
  const scanTs = (dir) => {
    for (const entry of readdirSync(dir)) {
      const full = join(dir, entry);
      const st = statSync(full);
      if (st.isDirectory()) {
        scanTs(full);
      } else if (
        /\.(tsx?|html)$/.test(full) &&
        !full.endsWith('.test.ts') &&
        !full.endsWith('.test.tsx')
      ) {
        const txt = readFileSync(full, 'utf8');
        if (/#0078d4/i.test(txt)) {
          allViolations.push(
            `${relative(webRoot, full)}: Microsoft brand blue (#0078D4) is forbidden.`,
          );
        }
      }
    }
  };
  scanTs(srcDir);

  if (allViolations.length > 0) {
    console.error(`✕ ${allViolations.length} brand token lint violation(s):`);
    for (const v of allViolations) console.error(`  ${v}`);
    process.exit(1);
  }
  console.log(
    `✓ ${cssFiles.length} CSS file(s) checked; all styles use docs/brand/tokens.css variables.`,
  );
}
