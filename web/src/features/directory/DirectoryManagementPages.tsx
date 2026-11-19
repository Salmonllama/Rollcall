// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import { FolderTree, ShieldCheck, Ticket } from 'lucide-react';
import { apiRequest, ApiError, formatUiDateTime } from '../../api/client';
import type {
  ConditionalAccessPolicyDto,
  DirectoryGroupDto,
  LicensesOverviewDto,
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
import { ChangePreview, DestructiveConfirmDialog } from '../../components/SignatureComponents';

export function GroupsPage() {
  const [q, setQ] = useState('');
  const query = useQuery({
    queryKey: ['groups', q],
    queryFn: () =>
      apiRequest<DirectoryGroupDto[]>(
        q.trim() ? `/api/groups?q=${encodeURIComponent(q.trim())}` : '/api/groups',
      ),
  });

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Groups</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Security groups, Microsoft 365 groups, distribution lists, and synced AD groups.
          </p>
        </div>
      </div>

      <div className="rc-card">
        <FormField id="group-search" label="Filter groups">
          <input
            id="group-search"
            className="rc-input"
            value={q}
            onChange={(e) => {
              setQ(e.target.value);
            }}
            placeholder="Group name"
          />
        </FormField>
      </div>

      <div className="rc-table-wrap">
        {(query.data ?? []).length === 0 ? (
          <EmptyState
            icon={<FolderTree size={48} strokeWidth={1.5} />}
            message="No groups matched your filter."
          />
        ) : (
          <table className="rc-table">
            <thead>
              <tr>
                <th>Display name</th>
                <th>Type</th>
                <th>Email</th>
                <th>Source</th>
                <th>Members</th>
              </tr>
            </thead>
            <tbody>
              {(query.data ?? []).map((g) => (
                <tr key={g.id}>
                  <td>
                    <strong>{g.displayName}</strong>
                  </td>
                  <td>{g.groupType}</td>
                  <td className="rc-mono">{g.mail ?? '—'}</td>
                  <td>{g.onPremisesSyncEnabled ? 'Active Directory' : 'Cloud'}</td>
                  <td>{g.memberCount}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}

export function LicensesPage() {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['licenses'],
    queryFn: () => apiRequest<LicensesOverviewDto>('/api/licenses'),
  });
  const [preview, setPreview] = useState<PlanSummaryDto | null>(null);
  const [bulkPayload, setBulkPayload] = useState<{
    userIds: string[];
    removeSkuIds: string[];
  } | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);

  const data = query.data;

  const planRemoveFromDisabled = async () => {
    if (!data || data.licensedDisabledUsers.length === 0) return;
    const userIds = data.licensedDisabledUsers.map((u) => u.userId);
    const removeSkuIds = [
      ...new Set(
        data.licensedDisabledUsers.flatMap((u) =>
          u.licenses.filter((l) => !l.assignedViaGroup).map((l) => l.skuId),
        ),
      ),
    ];
    const res = await apiRequest<{ plan: PlanSummaryDto }>('/api/licenses/change', {
      method: 'POST',
      body: { userIds, addSkuIds: [], removeSkuIds, submit: false },
    });
    setBulkPayload({ userIds, removeSkuIds });
    setPreview(res.plan);
  };

  const confirmBulkRemove = async () => {
    if (!bulkPayload) return;
    const res = await apiRequest<{ run: WorkflowRunDto | null }>('/api/licenses/change', {
      method: 'POST',
      body: { ...bulkPayload, addSkuIds: [], submit: true },
    });
    setConfirmOpen(false);
    if (res.run) void navigate(`/runs/${res.run.id}`);
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Licenses</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Subscribed SKUs, seat availability, and licenses still assigned to disabled accounts.
          </p>
        </div>
      </div>

      <div className="rc-table-wrap">
        <table className="rc-table">
          <thead>
            <tr>
              <th>License</th>
              <th>SKU part number</th>
              <th>Total</th>
              <th>Assigned</th>
              <th>Available</th>
            </tr>
          </thead>
          <tbody>
            {(data?.skus ?? []).map((s) => (
              <tr key={s.skuId}>
                <td>
                  <strong>{s.friendlyName}</strong>
                </td>
                <td className="rc-mono">{s.skuPartNumber}</td>
                <td>{s.totalSeats.toLocaleString()}</td>
                <td>{s.consumedSeats.toLocaleString()}</td>
                <td>
                  <StatusBadge
                    kind={
                      s.availableSeats === 0
                        ? 'failed'
                        : s.availableSeats < 5
                          ? 'needs-attention'
                          : 'succeeded'
                    }
                    label={`${String(s.availableSeats)} available`}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <section className="rc-card">
        <div className="rc-row-between">
          <h2 className="rc-section-title">
            Licenses on disabled accounts ({String(data?.licensedDisabledUsers.length ?? 0)})
          </h2>
          {data && data.licensedDisabledUsers.length > 0 ? (
            <Button variant="danger" onClick={() => void planRemoveFromDisabled()}>
              Remove licenses from {data.licensedDisabledUsers.length} disabled accounts
            </Button>
          ) : null}
        </div>

        {preview && bulkPayload ? (
          <>
            <ChangePreview
              plan={preview}
              primaryActionLabel={`Remove licenses from ${String(bulkPayload.userIds.length)} accounts`}
              onConfirm={() => {
                setConfirmOpen(true);
              }}
            />
            <DestructiveConfirmDialog
              open={confirmOpen}
              onOpenChange={setConfirmOpen}
              title="Remove licenses from disabled accounts"
              impactSentence={`This removes direct licenses from ${String(bulkPayload.userIds.length)} disabled accounts. Type the number of affected users to confirm.`}
              expectedText={String(bulkPayload.userIds.length)}
              inputLabel="Number of affected users"
              confirmButtonLabel={`Remove from ${String(bulkPayload.userIds.length)} accounts`}
              onConfirm={() => void confirmBulkRemove()}
            />
          </>
        ) : null}

        {(data?.licensedDisabledUsers ?? []).length === 0 ? (
          <EmptyState
            icon={<Ticket size={48} strokeWidth={1.5} />}
            message="No disabled accounts hold paid licenses."
          />
        ) : (
          <div className="rc-stack">
            {(data?.licensedDisabledUsers ?? []).map((u) => (
              <div className="rc-row-between" key={u.userId}>
                <div>
                  <strong>{u.displayName}</strong>{' '}
                  <span className="rc-mono rc-muted">{u.userPrincipalName}</span>
                </div>
                <span>{u.licenses.map((l) => l.friendlyName).join(', ')}</span>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

export function ConditionalAccessPage({ isAdmin }: { isAdmin: boolean }) {
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['ca-policies'],
    queryFn: () => apiRequest<ConditionalAccessPolicyDto[]>('/api/conditional-access'),
  });

  const [selectedPolicyId, setSelectedPolicyId] = useState('');
  const [excludeUserId, setExcludeUserId] = useState('');
  const [expiresAt, setExpiresAt] = useState('');
  const [preview, setPreview] = useState<PlanSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  const planTempExclusion = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    try {
      const res = await apiRequest<{ plan: PlanSummaryDto }>(
        '/api/conditional-access/temporary-exclusion',
        {
          method: 'POST',
          body: {
            policyId: selectedPolicyId,
            userId: excludeUserId.trim(),
            expiresAt: new Date(expiresAt).toISOString(),
            reason: 'Temporary exclusion',
            submit: false,
          },
        },
      );
      setPreview(res.plan);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not plan temporary exclusion.');
    }
  };

  const submitTempExclusion = async () => {
    setError(null);
    try {
      const res = await apiRequest<{ run: WorkflowRunDto | null }>(
        '/api/conditional-access/temporary-exclusion',
        {
          method: 'POST',
          body: {
            policyId: selectedPolicyId,
            userId: excludeUserId.trim(),
            expiresAt: new Date(expiresAt).toISOString(),
            reason: 'Temporary exclusion',
            submit: true,
          },
        },
      );
      if (res.run) void navigate(`/runs/${res.run.id}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Submission failed.');
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Conditional Access</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Inspect policies and manage user/group include and exclude targets with safety rails.
          </p>
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      {isAdmin ? (
        <form className="rc-card" onSubmit={(e) => void planTempExclusion(e)}>
          <h2 className="rc-section-title">Add temporary exclusion</h2>
          <div className="rc-grid-4">
            <FormField id="ca-policy" label="Policy">
              <select
                id="ca-policy"
                className="rc-select"
                value={selectedPolicyId}
                onChange={(e) => {
                  setSelectedPolicyId(e.target.value);
                }}
                required
              >
                <option value="">Select policy…</option>
                {(query.data ?? []).map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.displayName} ({p.state})
                  </option>
                ))}
              </select>
            </FormField>
            <FormField id="ca-user" label="User ID">
              <input
                id="ca-user"
                className="rc-input rc-input-mono"
                value={excludeUserId}
                onChange={(e) => {
                  setExcludeUserId(e.target.value);
                }}
                required
              />
            </FormField>
            <FormField id="ca-exp" label="Expires at">
              <input
                id="ca-exp"
                type="datetime-local"
                className="rc-input"
                value={expiresAt}
                onChange={(e) => {
                  setExpiresAt(e.target.value);
                }}
                required
              />
            </FormField>
          </div>
          <div className="rc-row">
            <Button type="submit" variant="primary">
              Review exclusion
            </Button>
          </div>
        </form>
      ) : null}

      {preview ? (
        <ChangePreview plan={preview} onConfirm={() => void submitTempExclusion()} />
      ) : null}

      <div className="rc-stack">
        {(query.data ?? []).map((p) => (
          <section className="rc-card" key={p.id}>
            <div className="rc-row-between">
              <div className="rc-row">
                <ShieldCheck size={20} aria-hidden="true" />
                <h2 className="rc-section-title">{p.displayName}</h2>
              </div>
              <StatusBadge
                kind={
                  p.state === 'enabled'
                    ? 'succeeded'
                    : p.state === 'disabled'
                      ? 'paused'
                      : 'scheduled'
                }
                label={p.state}
              />
            </div>
            <div className="rc-muted">
              Conditions (read-only): {p.conditionsSummary} · Grant controls:{' '}
              {p.grantControls.join(', ') || 'None'}
            </div>
            <div className="rc-row rc-muted">
              <span>Included users: {p.includeUsers.length}</span>
              <span>Excluded users: {p.excludeUsers.length}</span>
              <span>Included groups: {p.includeGroups.length}</span>
              <span>Excluded groups: {p.excludeGroups.length}</span>
            </div>
            {p.activeTemporaryExclusions.length > 0 ? (
              <div className="rc-stack">
                <span className="rc-eyebrow">Active temporary exclusions</span>
                {p.activeTemporaryExclusions.map((ex) => (
                  <div key={ex.id} className="rc-row-between">
                    <span className="rc-mono">{ex.targetRef}</span>
                    <span className="rc-muted">Expires {formatUiDateTime(ex.dueAt)}</span>
                  </div>
                ))}
              </div>
            ) : null}
          </section>
        ))}
      </div>
    </div>
  );
}
