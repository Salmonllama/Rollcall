// SPDX-License-Identifier: GPL-3.0-or-later
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

export default defineConfig({
  plugins: [react()],
  server: {
    // The logo and favicon come from docs/brand, the single source of brand assets.
    fs: { allow: ['.', '../docs/brand'] },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
  },
});
