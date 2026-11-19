// SPDX-License-Identifier: GPL-3.0-or-later
import { createContext, useContext } from 'react';

export type ThemeMode = 'system' | 'light' | 'dark';
export type DensityMode = 'default' | 'compact';

export interface ThemeContextValue {
  theme: ThemeMode;
  setTheme: (mode: ThemeMode) => void;
  resolvedTheme: 'light' | 'dark';
  density: DensityMode;
  setDensity: (mode: DensityMode) => void;
}

export const ThemeContext = createContext<ThemeContextValue>({
  theme: 'system',
  setTheme: () => undefined,
  resolvedTheme: 'light',
  density: 'default',
  setDensity: () => undefined,
});

export function useTheme(): ThemeContextValue {
  return useContext(ThemeContext);
}
