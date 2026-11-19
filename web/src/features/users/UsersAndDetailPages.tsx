// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Ban, KeyRound, RefreshCw, ShieldCheck, UserMinus, Users } from 'lucide-react';
import { apiRequest, ApiError, formatUiDateTime } from '../../api/client';
import type {
  DirectoryUserDetailDto,
  DirectoryUserDto,
  PlanSummaryDto,
  WorkflowRunDto,
} from '../../api/types';
import {
  AlertBanner,
  Button,
  EmptyState,
  FormField,
  StatusBadge,
} from '../../components/Primitives';
import { mapRunStatusToBrand } from '../../components/statusMapping';
import { ChangePreview } from '../../components/SignatureComponents';

export function UsersPage() {
  const [params, setParams] = useSearchParams();
  const [q, setQ] = useState(params.get('q') ?? '');
  const [department, setDepartment] = useState('');
  const [enabled, setEnabled] = useState('');
  const [synced, setSynced] = useState('');
  const [licensed, setLicensed] = useState('');

  const qs = new URLSearchParams();
  if (q.trim()) qs.set('q', q.trim());
  if (department.trim()) qs.set('department', department.trim());
  if (enabled) qs.set('enabled', enabled);
  if (synced) qs.set('synced', synced);
  if (licensed) qs.set('licensed', licensed);

  const usersQuery = useQuery({
    queryKey: ['users', qs.toString()],
    queryFn: () => apiRequest<DirectoryUserDto[]>(`/api/users?${qs.toString()}`),
  });

  const handleSearch = (e: React.SyntheticEvent) => {
    e.preventDefault();
    setParams(q.trim() ? { q: q.trim() } : {});
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Users</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Search across Microsoft Entra ID by display name, UPN, email, or employee ID.
          </p>
        </div>
        <Link to="/onboard" className="rc-btn rc-btn-primary">
          Onboard user
        </Link>
      </div>

      <form className="rc-card" onSubmit={handleSearch}>
        <div className="rc-grid-4">
          <FormField id="user-q" label="Search">
            <input
              id="user-q"
              className="rc-input"
              value={q}
              onChange={(e) => {
                setQ(e.target.value);
              }}
              placeholder="Name, UPN, or employee ID"
            />
          </FormField>
          <FormField id="user-dept" label="Department" optional>
            <input
              id="user-dept"
              className="rc-input"
              value={department}
              onChange={(e) => {
                setDepartment(e.target.value);
              }}
            />
          </FormField>
          <FormField id="user-enabled" label="Sign-in status" optional>
            <select
              id="user-enabled"
              className="rc-select"
              value={enabled}
              onChange={(e) => {
                setEnabled(e.target.value);
              }}
            >
              <option value="">All</option>
              <option value="true">Allowed</option>
              <option value="false">Blocked</option>
            </select>
          </FormField>
          <FormField id="user-synced" label="Source" optional>
            <select
              id="user-synced"
              className="rc-select"
              value={synced}
              onChange={(e) => {
                setSynced(e.target.value);
              }}
            >
              <option value="">Cloud and synced</option>
              <option value="false">Cloud-only</option>
              <option value="true">Synced from AD</option>
            </select>
          </FormField>
        </div>
        <div className="rc-row">
          <select
            aria-label="License filter"
            className="rc-select"
            style={{ width: 'auto' }}
            value={licensed}
            onChange={(e) => {
              setLicensed(e.target.value);
            }}
          >
            <option value="">Licensed and unlicensed</option>
            <option value="true">Licensed</option>
            <option value="false">Unlicensed</option>
          </select>
          <Button type="submit" variant="primary">
            Search users
          </Button>
          <Button
            variant="secondary"
            onClick={() =>
              void apiRequest('/api/users?refresh=true').then(() => usersQuery.refetch())
            }
          >
            <RefreshCw size={16} aria-hidden="true" />
            Refresh directory
          </Button>
        </div>
      </form>

      <div className="rc-table-wrap">
        {usersQuery.data && usersQuery.data.length === 0 ? (
          <EmptyState
            icon={<Users size={48} strokeWidth={1.5} />}
            message="No users matched your search filters."
          />
        ) : (
          <table className="rc-table">
            <thead>
              <tr>
                <th>Display name</th>
                <th>UPN</th>
                <th>Department</th>
                <th>Source</th>
                <th>Status</th>
                <th>Licenses</th>
              </tr>
            </thead>
            <tbody>
              {(usersQuery.data ?? []).map((u) => (
                <tr key={u.id}>
                  <td>
                    <Link to={`/users/${encodeURIComponent(u.id)}`}>{u.displayName}</Link>
                  </td>
                  <td className="rc-mono">{u.userPrincipalName}</td>
                  <td>{u.department ?? '—'}</td>
                  <td>
                    <span className="rc-badge rc-badge-muted">
                      {u.onPremisesSyncEnabled
                        ? 'Synced (AD)'
                        : u.userType === 'Guest'
                          ? 'Guest'
                          : 'Cloud'}
                    </span>
                  </td>
                  <td>
                    <StatusBadge
                      kind={u.accountEnabled ? 'succeeded' : 'failed'}
                      label={u.accountEnabled ? 'Allowed' : 'Blocked'}
                    />
                  </td>
                  <td>{u.licenseCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

export function UserDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const detailQuery = useQuery({
    queryKey: ['user', id],
    queryFn: () => apiRequest<DirectoryUserDetailDto>(`/api/users/${encodeURIComponent(id)}`),
    enabled: Boolean(id),
  });

  const [jobTitle, setJobTitle] = useState('');
  const [department, setDepartment] = useState('');
  const [previewPlan, setPreviewPlan] = useState<PlanSummaryDto | null>(null);
  const [pendingAction, setPendingAction] = useState<(() => Promise<void>) | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const detail = detailQuery.data;
  if (detailQuery.isLoading) return <p className="rc-muted">Loading user…</p>;
  if (!detail) return <AlertBanner tone="danger">Couldn&apos;t load this user.</AlertBanner>;

  const u = detail.user;

  const planAction = async (url: string, body: Record<string, unknown>) => {
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<{ plan: PlanSummaryDto }>(url, {
        method: 'POST',
        body: { ...body, submit: false },
      });
      setPreviewPlan(res.plan);
      setPendingAction(() => async () => {
        setBusy(true);
        try {
          const submitted = await apiRequest<{ run: WorkflowRunDto | null }>(url, {
            method: 'POST',
            body: { ...body, submit: true },
          });
          setPreviewPlan(null);
          if (submitted.run) {
            void navigate(`/runs/${submitted.run.id}`);
          }
        } catch (err) {
          setError(err instanceof ApiError ? err.message : 'Action failed.');
        } finally {
          setBusy(false);
        }
      });
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not plan changes.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <div className="rc-row">
            <h1 className="rc-page-title">{u.displayName}</h1>
            <StatusBadge
              kind={u.accountEnabled ? 'succeeded' : 'failed'}
              label={u.accountEnabled ? 'Sign-in allowed' : 'Sign-in blocked'}
            />
            <span className="rc-badge rc-badge-muted">
              {u.onPremisesSyncEnabled ? 'Synced (AD)' : u.userType === 'Guest' ? 'Guest' : 'Cloud'}
            </span>
          </div>
          <p className="rc-mono rc-muted" style={{ margin: 0 }}>
            {u.userPrincipalName} {u.employeeId ? `· ${u.employeeId}` : ''}
          </p>
        </div>
        <div className="rc-row">
          <Button
            variant="secondary"
            onClick={() =>
              void planAction(`/api/users/${encodeURIComponent(u.id)}/mfa/tap`, {
                lifetimeInMinutes: 60,
                isUsableOnce: true,
              })
            }
          >
            <KeyRound size={16} aria-hidden="true" />
            Issue TAP
          </Button>
          <Button
            variant="secondary"
            onClick={() =>
              void planAction(`/api/users/${encodeURIComponent(u.id)}/revoke-sessions`, {})
            }
          >
            <Ban size={16} aria-hidden="true" />
            Revoke sessions
          </Button>
          <Link to={`/offboard/${encodeURIComponent(u.id)}`} className="rc-btn rc-btn-danger">
            <UserMinus size={16} aria-hidden="true" />
            Offboard user
          </Link>
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      {previewPlan && pendingAction ? (
        <ChangePreview
          plan={previewPlan}
          isSubmitting={busy}
          onConfirm={() => void pendingAction()}
          secondaryAction={
            <Button
              variant="secondary"
              onClick={() => {
                setPreviewPlan(null);
              }}
            >
              Cancel
            </Button>
          }
        />
      ) : null}

      <div className="rc-grid-2">
        <section className="rc-card">
          <h2 className="rc-section-title">Attributes</h2>
          <div className="rc-stack" style={{ fontSize: 'var(--rc-text-sm)' }}>
            <div>
              Title: <strong>{u.jobTitle ?? '—'}</strong>
            </div>
            <div>
              Department: <strong>{u.department ?? '—'}</strong>
            </div>
            <div>
              Office: <strong>{u.officeLocation ?? '—'}</strong>
            </div>
            <div>
              Usage location: <span className="rc-mono">{u.usageLocation ?? '—'}</span>
            </div>
            <div>
              Manager:{' '}
              <strong>
                {detail.manager
                  ? `${detail.manager.displayName} (${detail.manager.userPrincipalName})`
                  : '—'}
              </strong>
            </div>
          </div>
          <div className="rc-grid-2">
            <FormField id="edit-title" label="New job title" optional>
              <input
                id="edit-title"
                className="rc-input"
                value={jobTitle}
                onChange={(e) => {
                  setJobTitle(e.target.value);
                }}
                placeholder={u.jobTitle ?? ''}
              />
            </FormField>
            <FormField id="edit-dept" label="New department" optional>
              <input
                id="edit-dept"
                className="rc-input"
                value={department}
                onChange={(e) => {
                  setDepartment(e.target.value);
                }}
                placeholder={u.department ?? ''}
              />
            </FormField>
          </div>
          <div className="rc-row">
            <Button
              variant="primary"
              disabled={busy || (!jobTitle.trim() && !department.trim())}
              onClick={() =>
                void planAction(`/api/users/${encodeURIComponent(u.id)}/attributes`, {
                  jobTitle: jobTitle.trim() || null,
                  department: department.trim() || null,
                })
              }
            >
              Review attribute changes
            </Button>
          </div>
        </section>

        <section className="rc-card">
          <h2 className="rc-section-title">Licenses ({detail.licenses.length})</h2>
          {detail.licenses.length === 0 ? (
            <p className="rc-muted">No licenses assigned.</p>
          ) : (
            <div className="rc-stack">
              {detail.licenses.map((lic) => (
                <div className="rc-row-between" key={lic.skuId}>
                  <div>
                    <strong>{lic.friendlyName}</strong>
                    {lic.assignedViaGroup ? (
                      <div className="rc-muted">Assigned via group {lic.sourceGroupName}</div>
                    ) : null}
                  </div>
                  {!lic.assignedViaGroup ? (
                    <Button
                      variant="ghost"
                      compact
                      onClick={() =>
                        void planAction('/api/licenses/change', {
                          userIds: [u.id],
                          addSkuIds: [],
                          removeSkuIds: [lic.skuId],
                        })
                      }
                    >
                      Remove
                    </Button>
                  ) : null}
                </div>
              ))}
            </div>
          )}
        </section>

        <section className="rc-card">
          <h2 className="rc-section-title">Groups ({detail.groups.length})</h2>
          {detail.groups.length === 0 ? (
            <p className="rc-muted">No direct group memberships.</p>
          ) : (
            <div className="rc-stack">
              {detail.groups.map((g) => (
                <div className="rc-row-between" key={g.id}>
                  <div>
                    <strong>{g.displayName}</strong>{' '}
                    <span className="rc-badge rc-badge-muted">{g.groupType}</span>
                  </div>
                  <Button
                    variant="ghost"
                    compact
                    onClick={() =>
                      void planAction('/api/groups/membership', {
                        userIds: [u.id],
                        groupIds: [g.id],
                        action: 'remove',
                      })
                    }
                  >
                    Remove
                  </Button>
                </div>
              ))}
            </div>
          )}
        </section>

        <section className="rc-card">
          <div className="rc-row-between">
            <h2 className="rc-section-title">MFA methods ({detail.mfaMethods.length})</h2>
            <Button
              variant="secondary"
              compact
              onClick={() =>
                void planAction(
                  `/api/users/${encodeURIComponent(u.id)}/mfa/require-reregistration`,
                  {},
                )
              }
            >
              Require re-registration
            </Button>
          </div>
          {detail.mfaMethods.length === 0 ? (
            <p className="rc-muted">No registered MFA methods.</p>
          ) : (
            <div className="rc-stack">
              {detail.mfaMethods.map((m) => (
                <div className="rc-row-between" key={m.id}>
                  <div>
                    <strong>{m.displayName}</strong>{' '}
                    <span className="rc-mono rc-muted">{m.detail ?? m.methodType}</span>
                  </div>
                  {m.removable ? (
                    <Button
                      variant="ghost"
                      compact
                      onClick={() =>
                        void planAction(`/api/users/${encodeURIComponent(u.id)}/mfa/remove`, {
                          methodId: m.id,
                          methodType: m.methodType,
                        })
                      }
                    >
                      Remove
                    </Button>
                  ) : null}
                </div>
              ))}
            </div>
          )}
        </section>
      </div>

      <section className="rc-card">
        <div className="rc-row">
          <ShieldCheck size={20} aria-hidden="true" />
          <h2 className="rc-section-title">Conditional Access policies</h2>
        </div>
        <div className="rc-stack">
          {detail.conditionalAccessPolicies.map((p) => (
            <div className="rc-row-between" key={p.policyId}>
              <div>
                <strong>{p.displayName}</strong>
                <div className="rc-muted">{p.reason}</div>
              </div>
              <span className="rc-badge rc-badge-info">{p.applicability}</span>
            </div>
          ))}
        </div>
      </section>

      <section className="rc-card">
        <h2 className="rc-section-title">Recent runs and audit history</h2>
        {detail.recentRuns.length === 0 ? (
          <p className="rc-muted">No Rollcall runs recorded for this user yet.</p>
        ) : (
          <div className="rc-stack">
            {detail.recentRuns.map((r) => {
              const b = mapRunStatusToBrand(r.status);
              return (
                <Link key={r.id} to={`/runs/${r.id}`} className="rc-row-between">
                  <span>{r.type}</span>
                  <div className="rc-row">
                    <span className="rc-muted">{formatUiDateTime(r.createdAt)}</span>
                    <StatusBadge kind={b.kind} label={b.label} />
                  </div>
                </Link>
              );
            })}
          </div>
        )}
      </section>
    </div>
  );
}
