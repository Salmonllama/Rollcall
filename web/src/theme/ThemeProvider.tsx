// SPDX-License-Identifier: GPL-3.0-or-later
import { useEffect, useState, type ReactNode } from 'react';
import { ThemeContext, type DensityMode, type ThemeMode } from './themeState';

const THEME_STORAGE_KEY = 'rollcall.theme';
const DENSITY_STORAGE_KEY = 'rollcall.density';

function getStoredTheme(): ThemeMode {
  try {
    const val = window.localStorage.getItem(THEME_STORAGE_KEY);
    if (val === 'light' || val === 'dark' || val === 'system') return val;
  } catch {
    // Ignore storage failures in restricted contexts.
  }
  return 'system';
}

function getStoredDensity(): DensityMode {
  try {
    const val = window.localStorage.getItem(DENSITY_STORAGE_KEY);
    if (val === 'compact' || val === 'default') return val;
  } catch {
    // Ignore storage failures.
  }
  return 'default';
}

function getSystemDark(): boolean {
  return typeof window !== 'undefined' && typeof window.matchMedia === 'function'
    ? window.matchMedia('(prefers-color-scheme: dark)').matches
    : false;
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [theme, setThemeState] = useState<ThemeMode>(getStoredTheme);
  const [density, setDensityState] = useState<DensityMode>(getStoredDensity);
  const [systemDark, setSystemDark] = useState<boolean>(getSystemDark);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = (e: MediaQueryListEvent) => {
      setSystemDark(e.matches);
    };
    mq.addEventListener('change', onChange);
    return () => {
      mq.removeEventListener('change', onChange);
    };
  }, []);

  const resolvedTheme: 'light' | 'dark' =
    theme === 'system' ? (systemDark ? 'dark' : 'light') : theme;

  useEffect(() => {
    const root = document.documentElement;
    if (theme === 'system') {
      root.removeAttribute('data-theme');
    } else {
      root.setAttribute('data-theme', theme);
    }
    root.setAttribute('data-density', density);
  }, [theme, density]);

  const setTheme = (mode: ThemeMode) => {
    setThemeState(mode);
    try {
      window.localStorage.setItem(THEME_STORAGE_KEY, mode);
    } catch {
      // Ignore storage errors.
    }
  };

  const setDensity = (mode: DensityMode) => {
    setDensityState(mode);
    try {
      window.localStorage.setItem(DENSITY_STORAGE_KEY, mode);
    } catch {
      // Ignore storage errors.
    }
  };

  return (
    <ThemeContext.Provider value={{ theme, setTheme, resolvedTheme, density, setDensity }}>
      {children}
    </ThemeContext.Provider>
  );
}
