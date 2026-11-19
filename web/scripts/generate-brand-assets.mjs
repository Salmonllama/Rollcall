#!/usr/bin/env node
// SPDX-License-Identifier: GPL-3.0-or-later
// Generates PNG favicons/PWA icons (16, 32, 180, 192, 512px) and the 1280x640 GitHub social
// preview image from the Rollcall brand mark geometry and palette (brand-guide.md §3, §14).
// Uses only Node.js built-in zlib (zero native dependencies, 100% GPLv3-compatible).

import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync } from 'node:zlib';

const webRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = join(webRoot, '..');
const iconsDir = join(webRoot, 'public', 'icons');
mkdirSync(iconsDir, { recursive: true });

// Brand palette (brand-guide.md §4)
const TEAL = [0x0e, 0x6a, 0x73, 0xff];
const MARIGOLD = [0xf0, 0xae, 0x48, 0xff];
const WHITE = [0xff, 0xff, 0xff, 0xff];
const BG = [0xf6, 0xf8, 0xf8, 0xff];
const INK = [0x13, 0x25, 0x2a, 0xff];
const BORDER = [0xd3, 0xde, 0xdf, 0xff];

const crcTable = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) !== 0 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function pngChunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 4, 'ascii');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}

function encodePng(width, height, rgba) {
  const sig = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const raw = Buffer.alloc(height * (1 + width * 4));
  for (let y = 0; y < height; y++) {
    const rowOff = y * (1 + width * 4);
    raw[rowOff] = 0;
    rgba.copy(raw, rowOff + 1, y * width * 4, (y + 1) * width * 4);
  }
  return Buffer.concat([
    sig,
    pngChunk('IHDR', ihdr),
    pngChunk('IDAT', deflateSync(raw, { level: 9 })),
    pngChunk('IEND', Buffer.alloc(0)),
  ]);
}

function fillRoundedRect(buf, w, h, x0, y0, rw, rh, r, color) {
  for (let y = Math.max(0, Math.floor(y0)); y < Math.min(h, Math.ceil(y0 + rh)); y++) {
    for (let x = Math.max(0, Math.floor(x0)); x < Math.min(w, Math.ceil(x0 + rw)); x++) {
      const dx = Math.max(x0 + r - x, 0, x - (x0 + rw - r - 1));
      const dy = Math.max(y0 + r - y, 0, y - (y0 + rh - r - 1));
      if (dx * dx + dy * dy <= r * r + 0.5) {
        const off = (y * w + x) * 4;
        buf[off] = color[0];
        buf[off + 1] = color[1];
        buf[off + 2] = color[2];
        buf[off + 3] = color[3];
      }
    }
  }
}

function drawMark(buf, w, h, ox, oy, size) {
  const s = size / 48;
  fillRoundedRect(buf, w, h, ox, oy, size, size, Math.max(2, Math.round(11 * s)), TEAL);
  if (size < 24) {
    // Simplified favicon mark: teal tile + marigold bar
    fillRoundedRect(
      buf,
      w,
      h,
      ox + 10 * s,
      oy + 20 * s,
      28 * s,
      8 * s,
      Math.max(1, 3 * s),
      MARIGOLD,
    );
    return;
  }
  // Three register rows; top row checked in marigold
  fillRoundedRect(buf, w, h, ox + 10 * s, oy + 12 * s, 6 * s, 6 * s, Math.max(1, 2 * s), MARIGOLD);
  fillRoundedRect(buf, w, h, ox + 20 * s, oy + 13 * s, 18 * s, 4 * s, Math.max(1, 2 * s), WHITE);
  fillRoundedRect(buf, w, h, ox + 10 * s, oy + 22 * s, 5 * s, 5 * s, Math.max(1, 2 * s), WHITE);
  fillRoundedRect(
    buf,
    w,
    h,
    ox + 20 * s,
    oy + 23 * s,
    18 * s,
    3.5 * s,
    Math.max(1, 1.5 * s),
    WHITE,
  );
  fillRoundedRect(buf, w, h, ox + 10 * s, oy + 32 * s, 5 * s, 5 * s, Math.max(1, 2 * s), WHITE);
  fillRoundedRect(
    buf,
    w,
    h,
    ox + 20 * s,
    oy + 33 * s,
    14 * s,
    3.5 * s,
    Math.max(1, 1.5 * s),
    WHITE,
  );
}

for (const size of [16, 32, 180, 192, 512]) {
  const rgba = Buffer.alloc(size * size * 4);
  drawMark(rgba, size, size, 0, 0, size);
  writeFileSync(join(iconsDir, `icon-${size}.png`), encodePng(size, size, rgba));
}

// PWA manifest
const manifest = {
  name: 'Rollcall',
  short_name: 'Rollcall',
  description: 'Self-hosted Microsoft 365 onboarding, offboarding, and identity management.',
  start_url: '/',
  display: 'standalone',
  background_color: '#F6F8F8',
  theme_color: '#0E6A73',
  icons: [
    { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
    { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
  ],
};
writeFileSync(
  join(webRoot, 'public', 'manifest.webmanifest'),
  `${JSON.stringify(manifest, null, 2)}\n`,
);

// 1280x640 GitHub social preview (brand-guide.md §14)
const sw = 1280;
const sh = 640;
const social = Buffer.alloc(sw * sh * 4);
for (let i = 0; i < sw * sh; i++) {
  social[i * 4] = BG[0];
  social[i * 4 + 1] = BG[1];
  social[i * 4 + 2] = BG[2];
  social[i * 4 + 3] = BG[3];
}
// Subtle framed card + Roster teal accent bar + Register mark
fillRoundedRect(social, sw, sh, 80, 72, 1120, 496, 20, BORDER);
fillRoundedRect(social, sw, sh, 82, 74, 1116, 492, 18, WHITE);
fillRoundedRect(social, sw, sh, 82, 74, 16, 492, 8, TEAL);
drawMark(social, sw, sh, 160, 210, 220);
// Stylized wordmark / preview lines to the right of the mark
fillRoundedRect(social, sw, sh, 430, 236, 460, 44, 10, INK);
fillRoundedRect(social, sw, sh, 430, 312, 620, 22, 6, TEAL);
fillRoundedRect(social, sw, sh, 430, 354, 520, 18, 6, BORDER);
fillRoundedRect(social, sw, sh, 430, 396, 180, 14, 5, MARIGOLD);
writeFileSync(join(repoRoot, 'docs', 'brand', 'social-preview.png'), encodePng(sw, sh, social));

console.log(
  '✓ Generated PWA icons (16, 32, 180, 192, 512px), manifest.webmanifest, and docs/brand/social-preview.png (1280x640).',
);
