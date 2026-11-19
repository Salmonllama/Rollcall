// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { Check, Copy, Download, ShieldCheck } from 'lucide-react';
import logoDark from '../../../../docs/brand/rollcall-logo-dark.svg';
import logoLight from '../../../../docs/brand/rollcall-logo.svg';
import { apiRequest, ApiError, formatUiDate } from '../../api/client';
import type { AppCertificateSummaryDto, PermissionCheckItemDto } from '../../api/types';
import { AlertBanner, Button, FormField, StatusBadge } from '../../components/Primitives';
import { useTheme } from '../../theme/themeState';

interface AppRegistrationGuide {
  hostname: string;
  redirectUri: string;
  certificateThumbprint: string;
  powerShellScript: string;
  permissions: {
    api: string;
    permission: string;
    directoryRole: string | null;
    feature: string;
    description: string;
    isEnabled: boolean;
  }[];
  directoryRoles: string[];
}

interface ValidationResult {
  succeeded: boolean;
  tenantId: string | null;
  displayName: string | null;
  primaryDomain: string | null;
  error: string | null;
  permissions: PermissionCheckItemDto[];
  directoryRoles: PermissionCheckItemDto[];
}

export function SetupWizardPage({ onComplete }: { onComplete: () => void }) {
  const { resolvedTheme } = useTheme();
  const [step, setStep] = useState(1);
  const [token, setToken] = useState('');
  const [cert, setCert] = useState<AppCertificateSummaryDto | null>(null);
  const [guide, setGuide] = useState<AppRegistrationGuide | null>(null);
  const [tenantId, setTenantId] = useState('');
  const [clientId, setClientId] = useState('');
  const [primaryDomain, setPrimaryDomain] = useState('');
  const [validation, setValidation] = useState<ValidationResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [copiedScript, setCopiedScript] = useState(false);

  const verifyToken = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      await apiRequest('/api/setup/verify-token', { method: 'POST', body: { token } });
      setStep(2);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Invalid setup token.');
    } finally {
      setBusy(false);
    }
  };

  const generateCert = async () => {
    setError(null);
    setBusy(true);
    try {
      const created = await apiRequest<AppCertificateSummaryDto>('/api/setup/certificate', {
        method: 'POST',
      });
      setCert(created);
      const regGuide = await apiRequest<AppRegistrationGuide>('/api/setup/app-registration-guide');
      setGuide(regGuide);
      setStep(3);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not generate certificate.');
    } finally {
      setBusy(false);
    }
  };

  const validateTenant = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<ValidationResult>('/api/setup/validate-connection', {
        method: 'POST',
        body: { tenantId, clientId, primaryDomain: primaryDomain || null },
      });
      setValidation(res);
      if (res.succeeded) setStep(5);
      else setError(res.error ?? 'Connection check failed.');
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Connection validation failed.');
    } finally {
      setBusy(false);
    }
  };

  const finishSetup = async () => {
    setError(null);
    setBusy(true);
    try {
      await apiRequest('/api/setup/complete', { method: 'POST', body: {} });
      onComplete();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Sign in with Entra ID first.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <main className="rc-content" style={{ maxWidth: '880px', margin: '0 auto' }}>
      <div className="rc-row-between">
        <img
          src={resolvedTheme === 'dark' ? logoDark : logoLight}
          alt="Rollcall"
          style={{ height: '32px' }}
        />
        <span className="rc-muted rc-tabular">Step {String(step)} of 5</span>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      {step === 1 ? (
        <form className="rc-card" onSubmit={(e) => void verifyToken(e)}>
          <h1 className="rc-page-title">Set up Rollcall</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Before you start, have a Global Administrator account ready for consent and your
            tenant&apos;s primary domain. Rollcall connects using an RSA 3072 certificate only — no
            client secrets or stored passwords.
          </p>
          <FormField
            id="setup-token"
            label="One-time setup token"
            helper="Printed by install.sh and stored in /opt/rollcall/.env."
          >
            <input
              id="setup-token"
              className="rc-input rc-input-mono"
              value={token}
              onChange={(e) => {
                setToken(e.target.value);
              }}
              required
              autoComplete="off"
            />
          </FormField>
          <div className="rc-row">
            <Button type="submit" variant="primary" disabled={busy || !token.trim()}>
              {busy ? 'Checking token…' : 'Continue to certificate'}
            </Button>
          </div>
        </form>
      ) : null}

      {step === 2 ? (
        <section className="rc-card">
          <h1 className="rc-page-title">Generate the app certificate</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Rollcall generates an RSA 3072 certificate in memory and encrypts the private key in the
            database. The private key never leaves this server.
          </p>
          <div className="rc-row">
            <Button variant="primary" onClick={() => void generateCert()} disabled={busy}>
              {busy ? 'Generating RSA 3072 key pair…' : 'Generate app certificate'}
            </Button>
          </div>
        </section>
      ) : null}

      {step === 3 && cert && guide ? (
        <section className="rc-card">
          <h1 className="rc-page-title">Create the Entra ID app registration</h1>
          <div className="rc-row-between">
            <div className="rc-stack">
              <span className="rc-eyebrow">Certificate thumbprint</span>
              <span className="rc-mono">{cert.thumbprint}</span>
              <span className="rc-muted">Expires {formatUiDate(cert.notAfter)}</span>
            </div>
            <a className="rc-btn rc-btn-secondary" href="/api/setup/certificate/download" download>
              <Download size={16} aria-hidden="true" />
              Download public .cer
            </a>
          </div>

          <div className="rc-grid-2">
            <div className="rc-card">
              <h2 className="rc-section-title">Option A: Guided PowerShell script</h2>
              <p className="rc-muted" style={{ margin: 0 }}>
                Run in Azure Cloud Shell or PowerShell 7 as a Global Administrator. It creates the
                app, uploads the certificate, grants consent, and assigns directory roles.
              </p>
              <pre className="rc-code-block" style={{ maxHeight: '260px' }}>
                {guide.powerShellScript}
              </pre>
              <Button
                variant="secondary"
                onClick={() => {
                  void navigator.clipboard.writeText(guide.powerShellScript);
                  setCopiedScript(true);
                }}
              >
                {copiedScript ? (
                  <Check size={16} aria-hidden="true" />
                ) : (
                  <Copy size={16} aria-hidden="true" />
                )}
                {copiedScript ? 'Copied script' : 'Copy PowerShell script'}
              </Button>
            </div>

            <div className="rc-card">
              <h2 className="rc-section-title">Option B: Manual portal steps</h2>
              <ol
                className="rc-stack"
                style={{ paddingLeft: '20px', margin: 0, fontSize: 'var(--rc-text-sm)' }}
              >
                <li>Create a single-tenant app registration in Microsoft Entra admin center.</li>
                <li>
                  Add Web redirect URI: <code className="rc-mono">{guide.redirectUri}</code>
                </li>
                <li>
                  Upload <code className="rc-mono">rollcall-app.cer</code> under Certificates.
                </li>
                <li>
                  Add application permissions ({String(guide.permissions.length)}) and grant admin
                  consent.
                </li>
                <li>Assign directory roles: {guide.directoryRoles.join(', ')}.</li>
              </ol>
            </div>
          </div>

          <div className="rc-row">
            <Button
              variant="primary"
              onClick={() => {
                setStep(4);
              }}
            >
              Continue to tenant connection
            </Button>
          </div>
        </section>
      ) : null}

      {step === 4 ? (
        <form className="rc-card" onSubmit={(e) => void validateTenant(e)}>
          <h1 className="rc-page-title">Connect your Microsoft 365 tenant</h1>
          <FormField id="tenant-id" label="Directory (tenant) ID">
            <input
              id="tenant-id"
              className="rc-input rc-input-mono"
              value={tenantId}
              onChange={(e) => {
                setTenantId(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="client-id" label="Application (client) ID">
            <input
              id="client-id"
              className="rc-input rc-input-mono"
              value={clientId}
              onChange={(e) => {
                setClientId(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="primary-domain" label="Primary domain" optional>
            <input
              id="primary-domain"
              className="rc-input rc-input-mono"
              value={primaryDomain}
              onChange={(e) => {
                setPrimaryDomain(e.target.value);
              }}
              placeholder="contoso.com"
            />
          </FormField>
          <div className="rc-row">
            <Button type="submit" variant="primary" disabled={busy}>
              {busy ? 'Checking connection…' : 'Validate tenant connection'}
            </Button>
          </div>
        </form>
      ) : null}

      {step === 5 && validation ? (
        <section className="rc-card">
          <h1 className="rc-page-title">Sign in as the first Admin</h1>
          <AlertBanner
            tone="success"
            title={`Connected to ${validation.displayName ?? validation.tenantId ?? ''}`}
          >
            Primary domain: <span className="rc-mono">{validation.primaryDomain}</span>
          </AlertBanner>

          <div className="rc-stack">
            <h2 className="rc-section-title">Permissions and directory roles</h2>
            {[...validation.permissions, ...validation.directoryRoles].map((item) => (
              <div className="rc-row-between" key={`${item.api}-${item.name}`}>
                <span className="rc-mono">{item.name}</span>
                <div className="rc-row">
                  {!item.granted ? (
                    <span className="rc-muted">Affects: {item.affectedFeatures.join(', ')}</span>
                  ) : null}
                  <StatusBadge
                    kind={item.granted ? 'succeeded' : 'needs-attention'}
                    label={item.granted ? 'Granted' : 'Missing'}
                  />
                </div>
              </div>
            ))}
          </div>

          <div className="rc-row">
            <a className="rc-btn rc-btn-primary" href="/api/auth/login?returnUrl=/setup">
              <ShieldCheck size={18} aria-hidden="true" />
              Sign in with Microsoft Entra ID
            </a>
            <Button variant="secondary" onClick={() => void finishSetup()} disabled={busy}>
              Finish setup
            </Button>
          </div>
        </section>
      ) : null}
    </main>
  );
}

export function SignInPage({ tenantDomain }: { tenantDomain: string | null }) {
  const { resolvedTheme } = useTheme();
  return (
    <main className="rc-content" style={{ maxWidth: '480px', margin: 'var(--rc-space-8) auto' }}>
      <section className="rc-card">
        <img
          src={resolvedTheme === 'dark' ? logoDark : logoLight}
          alt="Rollcall"
          style={{ height: '32px', alignSelf: 'flex-start' }}
        />
        <h1 className="rc-page-title">Sign in to Rollcall</h1>
        <p className="rc-muted" style={{ margin: 0 }}>
          Rollcall can&apos;t store passwords. Sign in with your Microsoft work account
          {tenantDomain ? (
            <>
              {' '}
              in <span className="rc-mono">{tenantDomain}</span>
            </>
          ) : null}
          .
        </p>
        <a className="rc-btn rc-btn-primary" href="/api/auth/login">
          Sign in with Microsoft Entra ID
        </a>
      </section>
    </main>
  );
}

export function NoAccessPage({ upn }: { upn: string | null }) {
  return (
    <main className="rc-content" style={{ maxWidth: '520px', margin: 'var(--rc-space-8) auto' }}>
      <section className="rc-card">
        <h1 className="rc-page-title">Ask your admin for access</h1>
        <p style={{ margin: 0 }}>
          You are signed in as <span className="rc-mono">{upn ?? 'your work account'}</span>, but
          you don&apos;t have a Rollcall role yet. Ask a Rollcall Admin to assign you the
          Technician, Approver, or Admin role in <strong>Admin › Team</strong>.
        </p>
      </section>
    </main>
  );
}
