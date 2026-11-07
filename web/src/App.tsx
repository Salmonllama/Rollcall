// SPDX-License-Identifier: GPL-3.0-or-later
import logoDarkUrl from '../../docs/brand/rollcall-logo-dark.svg';
import logoUrl from '../../docs/brand/rollcall-logo.svg';
import './placeholder.css';

// Placeholder page served by the installed stack. Fonts, the full theme, the app shell, the setup
// wizard, and routing arrive in M2.
export function App() {
  return (
    <main className="placeholder">
      <h1>
        <picture>
          <source srcSet={logoDarkUrl} media="(prefers-color-scheme: dark)" />
          <img src={logoUrl} alt="Rollcall" height={48} />
        </picture>
      </h1>
      <p>Rollcall is installed and running.</p>
      <p className="muted">
        The setup wizard isn't in this version yet. It arrives in the next release.
      </p>
    </main>
  );
}
