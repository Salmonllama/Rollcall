// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, NavLink } from 'react-router-dom';
import { ClipboardList, Copy, RefreshCw, Server } from 'lucide-react';
import { apiRequest, ApiError, formatUiDate, formatUiDateTime } from '../../api/client';
import type {
  AdAgentDto,
  ApprovalRuleDto,
  ConnectionsOverviewDto,
  HealthOverviewDto,
  PortalRole,
  PortalSettingsDto,
  PortalUserDto,
  RoleTemplateDto,
} from '../../api/types';
import {
  AlertBanner,
  Button,
  EmptyState,
  FormField,
  StatusBadge,
} from '../../components/Primitives';

export function AdminNavTabs() {
  const tabs = [
    { to: '/admin/settings', label: 'General & features' },
    { to: '/admin/templates', label: 'Role templates' },
    { to: '/admin/approval-rules', label: 'Approval rules' },
    { to: '/admin/team', label: 'Team' },
    { to: '/admin/connections', label: 'Connections & cert' },
    { to: '/admin/agents', label: 'AD agents' },
    { to: '/admin/health', label: 'Health' },
  ];
  return (
    <nav className="rc-row" aria-label="Admin sections">
      {tabs.map((t) => (
        <NavLink key={t.to} to={t.to} className="rc-nav-link">
          {t.label}
        </NavLink>
      ))}
    </nav>
  );
}

export function TemplatesPage() {
  const query = useQuery({
    queryKey: ['templates', 'all'],
    queryFn: () => apiRequest<RoleTemplateDto[]>('/api/templates?includeArchived=true'),
  });
  const [name, setName] = useState('');
  const [department, setDepartment] = useState('');
  const [jobTitle, setJobTitle] = useState('');
  const [error, setError] = useState<string | null>(null);

  const createTemplate = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    try {
      await apiRequest('/api/templates', {
        method: 'POST',
        body: {
          name: name.trim(),
          description: null,
          definition: {
            accountType: 'cloud',
            upnPattern: '{first}.{last}@{domain}',
            displayNamePattern: '{first} {last}',
            department: department.trim() || null,
            jobTitle: jobTitle.trim() || null,
            companyName: null,
            officeLocation: null,
            usageLocation: 'US',
            targetOu: null,
            cloudGroupIds: [],
            distributionListIds: [],
            adGroupDns: [],
            licenses: [],
            credentialMode: 'tap',
            tapLifetimeMinutes: 60,
            tapUsableOnce: true,
            createBlockedUntilStartDate: true,
            mailboxTimeZone: null,
            mailboxLanguage: null,
            managerNotificationText: null,
            overridableFields: ['department', 'jobTitle'],
            assignsAdminRoles: false,
          },
        },
      });
      setName('');
      setDepartment('');
      setJobTitle('');
      await query.refetch();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not save role template.');
    }
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Role templates</h1>
      <AdminNavTabs />
      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      <form className="rc-card" onSubmit={(e) => void createTemplate(e)}>
        <h2 className="rc-section-title">Create role template</h2>
        <div className="rc-grid-4">
          <FormField id="tpl-name" label="Template name">
            <input
              id="tpl-name"
              className="rc-input"
              value={name}
              onChange={(e) => {
                setName(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="tpl-dept" label="Default department" optional>
            <input
              id="tpl-dept"
              className="rc-input"
              value={department}
              onChange={(e) => {
                setDepartment(e.target.value);
              }}
            />
          </FormField>
          <FormField id="tpl-title" label="Default job title" optional>
            <input
              id="tpl-title"
              className="rc-input"
              value={jobTitle}
              onChange={(e) => {
                setJobTitle(e.target.value);
              }}
            />
          </FormField>
        </div>
        <div className="rc-row">
          <Button type="submit" variant="primary">
            Create template
          </Button>
        </div>
      </form>

      {(query.data ?? []).length === 0 ? (
        <section className="rc-card">
          <EmptyState
            icon={<ClipboardList size={48} strokeWidth={1.5} />}
            message="No role templates yet. Templates define what a job gets: groups, licenses, and policies."
          />
        </section>
      ) : (
        <div className="rc-stack">
          {(query.data ?? []).map((t) => (
            <section className="rc-card" key={t.id}>
              <div className="rc-row-between">
                <div>
                  <strong>{t.name}</strong>{' '}
                  <span className="rc-badge rc-badge-muted">v{t.version}</span>
                  {t.isArchived ? (
                    <span className="rc-badge rc-badge-warning">Archived</span>
                  ) : null}
                  <div className="rc-mono rc-muted">
                    {t.definition.upnPattern} · {t.definition.accountType}
                  </div>
                </div>
                <div className="rc-row">
                  <Button
                    variant="secondary"
                    compact
                    onClick={() =>
                      void apiRequest(`/api/templates/${t.id}/duplicate`, { method: 'POST' }).then(
                        () => query.refetch(),
                      )
                    }
                  >
                    Duplicate
                  </Button>
                  <Button
                    variant="ghost"
                    compact
                    onClick={() =>
                      void apiRequest(`/api/templates/${t.id}/archive`, { method: 'POST' }).then(
                        () => query.refetch(),
                      )
                    }
                  >
                    {t.isArchived ? 'Unarchive' : 'Archive'}
                  </Button>
                </div>
              </div>
            </section>
          ))}
        </div>
      )}
    </div>
  );
}

export function ApprovalRulesPage() {
  const query = useQuery({
    queryKey: ['approval-rules'],
    queryFn: () => apiRequest<ApprovalRuleDto[]>('/api/approval-rules'),
  });

  const toggle = async (rule: ApprovalRuleDto) => {
    await apiRequest(`/api/approval-rules/${rule.actionType}`, {
      method: 'PUT',
      body: { required: !rule.required, approverRole: rule.approverRole },
    });
    await query.refetch();
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Approval rules</h1>
      <AdminNavTabs />
      <div className="rc-card">
        {(query.data ?? []).map((rule) => (
          <div className="rc-row-between" key={rule.actionType}>
            <div>
              <strong>{rule.label}</strong>
              <div className="rc-muted">{rule.description}</div>
            </div>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={rule.required}
                disabled={rule.lockedRequired}
                onChange={() => void toggle(rule)}
              />
              {rule.lockedRequired ? 'Always required' : 'Require approval'}
            </label>
          </div>
        ))}
      </div>
    </div>
  );
}

export function TeamPage() {
  const query = useQuery({
    queryKey: ['team'],
    queryFn: () => apiRequest<PortalUserDto[]>('/api/admin/team'),
  });
  const [upn, setUpn] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [role, setRole] = useState<PortalRole>('Technician');

  const invite = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    await apiRequest('/api/admin/team', {
      method: 'POST',
      body: {
        upn: upn.trim(),
        displayName: displayName.trim() || null,
        entraObjectId: null,
        roles: [role],
      },
    });
    setUpn('');
    setDisplayName('');
    await query.refetch();
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Team and portal roles</h1>
      <AdminNavTabs />

      <form className="rc-card" onSubmit={(e) => void invite(e)}>
        <h2 className="rc-section-title">Grant portal access</h2>
        <div className="rc-grid-4">
          <FormField id="team-upn" label="Work email (UPN)">
            <input
              id="team-upn"
              className="rc-input rc-input-mono"
              value={upn}
              onChange={(e) => {
                setUpn(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="team-name" label="Display name" optional>
            <input
              id="team-name"
              className="rc-input"
              value={displayName}
              onChange={(e) => {
                setDisplayName(e.target.value);
              }}
            />
          </FormField>
          <FormField id="team-role" label="Role">
            <select
              id="team-role"
              className="rc-select"
              value={role}
              onChange={(e) => {
                setRole(e.target.value as PortalRole);
              }}
            >
              <option value="Technician">Technician</option>
              <option value="Approver">Approver</option>
              <option value="Admin">Admin</option>
            </select>
          </FormField>
        </div>
        <div className="rc-row">
          <Button type="submit" variant="primary">
            Grant access
          </Button>
        </div>
      </form>

      <div className="rc-table-wrap">
        <table className="rc-table">
          <thead>
            <tr>
              <th>Name</th>
              <th>UPN</th>
              <th>Roles</th>
              <th>Status</th>
              <th>Last sign-in</th>
            </tr>
          </thead>
          <tbody>
            {(query.data ?? []).map((u) => (
              <tr key={u.id}>
                <td>{u.displayName}</td>
                <td className="rc-mono">{u.upn}</td>
                <td>{u.roles.join(', ') || 'None'}</td>
                <td>
                  <StatusBadge
                    kind={u.disabled ? 'failed' : 'succeeded'}
                    label={u.disabled ? 'Disabled' : 'Active'}
                  />
                </td>
                <td>{formatUiDateTime(u.lastSignInAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function ConnectionsPage() {
  const query = useQuery({
    queryKey: ['connections'],
    queryFn: () => apiRequest<ConnectionsOverviewDto>('/api/admin/connections'),
  });
  const [rotationScript, setRotationScript] = useState<string | null>(null);
  const data = query.data;

  const startRotation = async () => {
    const res = await apiRequest<{ powerShellScript: string }>(
      '/api/admin/connections/certificates/rotate/start',
      { method: 'POST' },
    );
    setRotationScript(res.powerShellScript);
    await query.refetch();
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Tenant connection and certificate</h1>
      <AdminNavTabs />

      {data ? (
        <>
          <section className="rc-card">
            <div className="rc-row-between">
              <div>
                <h2 className="rc-section-title">{data.organizationName ?? 'Connected tenant'}</h2>
                <div className="rc-mono rc-muted">
                  Tenant: {data.tenantId} · App: {data.appClientId} · Domain: {data.primaryDomain}
                </div>
              </div>
              <Button variant="secondary" onClick={() => void query.refetch()}>
                <RefreshCw size={16} aria-hidden="true" />
                Re-check permissions
              </Button>
            </div>
            {data.activeCertificate ? (
              <div className="rc-row-between">
                <div>
                  <strong>Active certificate</strong>
                  <div className="rc-mono rc-muted">
                    {data.activeCertificate.thumbprint} · Expires{' '}
                    {formatUiDate(data.activeCertificate.notAfter)} (
                    {String(data.activeCertificate.daysUntilExpiry)} days)
                  </div>
                </div>
                <Button variant="primary" onClick={() => void startRotation()}>
                  Start certificate rotation
                </Button>
              </div>
            ) : null}

            {data.pendingCertificate ? (
              <AlertBanner
                tone="info"
                title="Pending certificate ready for activation"
                action={
                  <Button
                    variant="primary"
                    compact
                    onClick={() =>
                      void apiRequest('/api/admin/connections/certificates/rotate/activate', {
                        method: 'POST',
                        body: { certificateId: data.pendingCertificate?.id },
                      }).then(() => query.refetch())
                    }
                  >
                    Validate and activate
                  </Button>
                }
              >
                Upload thumbprint{' '}
                <span className="rc-mono">{data.pendingCertificate.thumbprint}</span> alongside the
                current certificate in Entra ID, then activate it.
              </AlertBanner>
            ) : null}

            {rotationScript ? <pre className="rc-code-block">{rotationScript}</pre> : null}
          </section>

          <section className="rc-card">
            <h2 className="rc-section-title">Permissions and directory roles</h2>
            {[...data.permissions, ...data.directoryRoles].map((p) => (
              <div className="rc-row-between" key={`${p.api}-${p.name}`}>
                <span className="rc-mono">{p.name}</span>
                <StatusBadge
                  kind={p.granted ? 'succeeded' : 'failed'}
                  label={p.granted ? 'Granted' : 'Missing'}
                />
              </div>
            ))}
          </section>
        </>
      ) : (
        <p className="rc-muted">Loading connection status…</p>
      )}
    </div>
  );
}

export function AgentsPage() {
  const query = useQuery({
    queryKey: ['agents'],
    queryFn: () => apiRequest<AdAgentDto[]>('/api/admin/agents'),
  });
  const [agentName, setAgentName] = useState('');
  const [enrollCmd, setEnrollCmd] = useState<string | null>(null);

  const createEnrollment = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    const res = await apiRequest<{ powerShellInstallCommand: string }>(
      '/api/admin/agents/enroll-command',
      { method: 'POST', body: { name: agentName.trim() } },
    );
    setEnrollCmd(res.powerShellInstallCommand);
    setAgentName('');
    await query.refetch();
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">On-prem AD agents</h1>
      <AdminNavTabs />

      <form className="rc-card" onSubmit={(e) => void createEnrollment(e)}>
        <h2 className="rc-section-title">Enroll a new AD agent</h2>
        <FormField id="agent-name" label="Agent display name">
          <input
            id="agent-name"
            className="rc-input"
            value={agentName}
            onChange={(e) => {
              setAgentName(e.target.value);
            }}
            placeholder="DC01 AD agent"
            required
          />
        </FormField>
        <div className="rc-row">
          <Button type="submit" variant="primary">
            Generate 1-hour install command
          </Button>
        </div>
        {enrollCmd ? (
          <div className="rc-stack">
            <pre className="rc-code-block">{enrollCmd}</pre>
            <Button
              variant="secondary"
              compact
              onClick={() => void navigator.clipboard.writeText(enrollCmd)}
            >
              <Copy size={16} aria-hidden="true" />
              Copy command
            </Button>
          </div>
        ) : null}
      </form>

      {(query.data ?? []).length === 0 ? (
        <section className="rc-card">
          <EmptyState
            icon={<Server size={48} strokeWidth={1.5} />}
            message="No AD agents enrolled yet. Enroll an agent on a domain-joined Windows Server for hybrid management."
          />
        </section>
      ) : (
        <div className="rc-table-wrap">
          <table className="rc-table">
            <thead>
              <tr>
                <th>Name</th>
                <th>Hostname</th>
                <th>Version</th>
                <th>Status</th>
                <th>Last seen</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {(query.data ?? []).map((a) => (
                <tr key={a.id}>
                  <td>
                    <strong>{a.name}</strong>
                  </td>
                  <td className="rc-mono">{a.hostname ?? '—'}</td>
                  <td className="rc-mono">{a.version ?? '—'}</td>
                  <td>
                    <StatusBadge
                      kind={
                        a.status === 'Connected'
                          ? 'succeeded'
                          : a.status === 'Pending'
                            ? 'queued'
                            : 'failed'
                      }
                      label={a.status}
                    />
                  </td>
                  <td>{formatUiDateTime(a.lastSeenAt)}</td>
                  <td>
                    {a.status !== 'Revoked' ? (
                      <Button
                        variant="ghost"
                        compact
                        onClick={() =>
                          void apiRequest(`/api/admin/agents/${a.id}/revoke`, {
                            method: 'POST',
                          }).then(() => query.refetch())
                        }
                      >
                        Revoke
                      </Button>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

export function SettingsPage() {
  const query = useQuery({
    queryKey: ['portal-settings'],
    queryFn: () => apiRequest<PortalSettingsDto>('/api/admin/settings'),
  });
  const [savedMsg, setSavedMsg] = useState<string | null>(null);
  const data = query.data;

  const toggleFeature = async (key: string) => {
    if (!data) return;
    const nextFeatures = { ...data.features, [key]: !data.features[key] };
    await apiRequest('/api/admin/settings', {
      method: 'PUT',
      headers: { 'If-Match': `"${data.eTag}"` },
      body: { ...data, features: nextFeatures },
    });
    setSavedMsg(`Updated feature setting for ${key}.`);
    await query.refetch();
  };

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Settings</h1>
      <AdminNavTabs />
      {savedMsg ? <AlertBanner tone="success">{savedMsg}</AlertBanner> : null}

      {data ? (
        <section className="rc-card">
          <h2 className="rc-section-title">Feature flags</h2>
          <p className="rc-muted" style={{ margin: 0 }}>
            Turning a feature off hides it in the UI, rejects its API routes, and removes its
            permissions from the manifest.
          </p>
          {Object.entries(data.features).map(([key, enabled]) => (
            <label className="rc-checkbox-label" key={key}>
              <input type="checkbox" checked={enabled} onChange={() => void toggleFeature(key)} />
              <span className="rc-mono">{key}</span>
            </label>
          ))}
        </section>
      ) : (
        <p className="rc-muted">Loading settings…</p>
      )}
    </div>
  );
}

export function HealthPage() {
  const query = useQuery({
    queryKey: ['admin-health'],
    queryFn: () => apiRequest<HealthOverviewDto>('/api/admin/health'),
  });
  const data = query.data;

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">System health</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Live diagnostics for database, worker, Graph, Exchange, certificate, agents, and
            backups.
          </p>
        </div>
        <Button variant="secondary" onClick={() => void query.refetch()}>
          <RefreshCw size={16} aria-hidden="true" />
          Check now
        </Button>
      </div>
      <AdminNavTabs />

      {data ? (
        <>
          <div className="rc-grid-4">
            <div className="rc-card">
              <span className="rc-eyebrow">Overall status</span>
              <StatusBadge
                kind={
                  data.overallStatus === 'healthy'
                    ? 'succeeded'
                    : data.overallStatus === 'degraded'
                      ? 'needs-attention'
                      : 'failed'
                }
                label={data.overallStatus}
              />
            </div>
            <div className="rc-card">
              <span className="rc-eyebrow">Job queue depth</span>
              <span className="rc-figure">{data.queueDepth}</span>
            </div>
            <div className="rc-card">
              <span className="rc-eyebrow">Failed runs (24 h)</span>
              <span className="rc-figure">{data.failedRunsLast24Hours}</span>
            </div>
            <div className="rc-card">
              <span className="rc-eyebrow">Last backup</span>
              <span>
                {data.lastBackupAt ? formatUiDateTime(data.lastBackupAt) : 'Not recorded yet'}
              </span>
            </div>
          </div>

          <section className="rc-card">
            {data.components.map((c) => (
              <div className="rc-row-between" key={c.name}>
                <div>
                  <strong>{c.name}</strong>
                  <div className="rc-muted">{c.summary}</div>
                  {c.detail ? <div className="rc-mono rc-muted">{c.detail}</div> : null}
                </div>
                <StatusBadge
                  kind={
                    c.status === 'ok'
                      ? 'succeeded'
                      : c.status === 'warning'
                        ? 'needs-attention'
                        : 'failed'
                  }
                  label={
                    c.status === 'ok' ? 'Healthy' : c.status === 'warning' ? 'Attention' : 'Failing'
                  }
                />
              </div>
            ))}
          </section>
        </>
      ) : (
        <p className="rc-muted">Checking health…</p>
      )}
    </div>
  );
}

export function HelpPage() {
  const links = [
    {
      title: 'Day-1 deployment guide',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/day-1/README.md',
    },
    {
      title: 'Administrator guide',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/admin/installer-commands.md',
    },
    {
      title: 'Technician guide',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/technician/onboarding.md',
    },
    {
      title: 'Permissions reference',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/reference/permissions.md',
    },
    {
      title: 'Troubleshooting guide',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/troubleshooting.md',
    },
    {
      title: 'Security threat model',
      href: 'https://github.com/Salmonllama/Rollcall/blob/main/docs/security/threat-model.md',
    },
  ];

  return (
    <div className="rc-stack">
      <h1 className="rc-page-title">Help and documentation</h1>
      <section className="rc-card">
        <p className="rc-muted" style={{ margin: 0 }}>
          Version-matched guides live in the Rollcall documentation set.
        </p>
        <div className="rc-stack">
          {links.map((l) => (
            <a key={l.title} href={l.href} target="_blank" rel="noreferrer">
              {l.title}
            </a>
          ))}
        </div>
        {import.meta.env.DEV ? (
          <div>
            <Link to="/dev/components">Open component reference (/dev/components)</Link>
          </div>
        ) : null}
      </section>
    </div>
  );
}
