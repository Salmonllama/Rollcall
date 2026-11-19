// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Download, RotateCcw, ScrollText, ShieldCheck, Stamp } from 'lucide-react';
import { apiRequest, ApiError, formatUiDateTime } from '../../api/client';
import type {
  AuditEventDto,
  CursorPage,
  WorkflowRunDto,
  WorkflowRunListItemDto,
} from '../../api/types';
import {
  AlertBanner,
  Button,
  CredentialDisplay,
  EmptyState,
  FormField,
  StatusBadge,
} from '../../components/Primitives';
import { mapRunStatusToBrand } from '../../components/statusMapping';
import {
  ApprovalBanner,
  ChangePreview,
  JobStepProgress,
} from '../../components/SignatureComponents';

export function RunsPage() {
  const [statusFilter, setStatusFilter] = useState('');
  const qs = statusFilter ? `?status=${encodeURIComponent(statusFilter)}` : '';
  const query = useQuery({
    queryKey: ['runs', statusFilter],
    queryFn: () => apiRequest<WorkflowRunListItemDto[]>(`/api/runs${qs}`),
  });

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Runs</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Every Rollcall change runs as an audited, retryable workflow.
          </p>
        </div>
        <select
          aria-label="Filter runs by status"
          className="rc-select"
          style={{ width: 'auto' }}
          value={statusFilter}
          onChange={(e) => {
            setStatusFilter(e.target.value);
          }}
        >
          <option value="">All statuses</option>
          <option value="AwaitingApproval">Awaiting approval</option>
          <option value="Scheduled">Scheduled</option>
          <option value="Running">Running</option>
          <option value="PartiallyFailed">Failed</option>
          <option value="NeedsReview">Needs attention</option>
          <option value="Succeeded">Succeeded</option>
        </select>
      </div>

      <div className="rc-table-wrap">
        <table className="rc-table">
          <thead>
            <tr>
              <th>Type</th>
              <th>Target</th>
              <th>Requested by</th>
              <th>Status</th>
              <th>Steps</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {(query.data ?? []).map((r) => {
              const brand = mapRunStatusToBrand(r.status);
              return (
                <tr key={r.id}>
                  <td>
                    <Link to={`/runs/${r.id}`}>{r.type}</Link>
                  </td>
                  <td className="rc-mono">{r.targetUserDisplayName ?? r.targetUserRef}</td>
                  <td>{r.requestedByDisplayName}</td>
                  <td>
                    <StatusBadge kind={brand.kind} label={brand.label} />
                  </td>
                  <td>
                    {r.completedSteps} / {r.totalSteps}
                  </td>
                  <td>{formatUiDateTime(r.createdAt)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
}

export function RunDetailPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const query = useQuery({
    queryKey: ['run', id],
    queryFn: () => apiRequest<WorkflowRunDto>(`/api/runs/${encodeURIComponent(id)}`),
    enabled: Boolean(id),
    refetchInterval: (q) => {
      const st = q.state.data?.status;
      return st === 'Queued' || st === 'Running' || st === 'WaitingForAgent' ? 3000 : false;
    },
  });

  const [credential, setCredential] = useState<{ kind: string; value: string } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const run = query.data;
  if (query.isLoading) return <p className="rc-muted">Loading run…</p>;
  if (!run) return <AlertBanner tone="danger">Run not found.</AlertBanner>;

  const brand = mapRunStatusToBrand(run.status);

  const decide = async (decision: 'Approved' | 'Rejected', comment: string) => {
    setError(null);
    setBusy(true);
    try {
      await apiRequest(`/api/approvals/${run.id}/decision`, {
        method: 'POST',
        body: { decision, comment: comment || null },
      });
      await query.refetch();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Approval decision failed.');
    } finally {
      setBusy(false);
    }
  };

  const retry = async () => {
    setError(null);
    setBusy(true);
    try {
      await apiRequest(`/api/runs/${run.id}/retry`, { method: 'POST' });
      await query.refetch();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Retry failed.');
    } finally {
      setBusy(false);
    }
  };

  const revealCredential = async () => {
    setError(null);
    try {
      const res = await apiRequest<{ credentialKind: string; value: string }>(
        `/api/runs/${run.id}/credential`,
        { method: 'POST' },
      );
      setCredential({ kind: res.credentialKind, value: res.value });
      await query.refetch();
    } catch (err) {
      setError(
        err instanceof ApiError ? err.message : 'Credential already shown; issue a new one.',
      );
    }
  };

  const restoreUser = async () => {
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<{ run: WorkflowRunDto | null }>('/api/offboarding/restore', {
        method: 'POST',
        body: {
          offboardingRunId: run.id,
          reEnableSignIn: true,
          restoreGroups: true,
          restoreLicenses: true,
          submit: true,
        },
      });
      if (res.run) void navigate(`/runs/${res.run.id}`);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Restore failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <div className="rc-row">
            <h1 className="rc-page-title">
              {run.type}: {run.targetUserDisplayName ?? run.targetUserRef}
            </h1>
            <StatusBadge kind={brand.kind} label={brand.label} />
          </div>
          <p className="rc-muted" style={{ margin: 0 }}>
            Requested by {run.requestedByDisplayName} on {formatUiDateTime(run.createdAt)}
            {run.roleTemplateVersion ? ` · Template v${String(run.roleTemplateVersion)}` : ''}
          </p>
        </div>
        <div className="rc-row">
          {run.type === 'Onboarding' && run.status === 'Succeeded' ? (
            <a
              className="rc-btn rc-btn-secondary"
              href={`/api/runs/${run.id}/welcome-pack.pdf`}
              download
            >
              <Download size={16} aria-hidden="true" />
              Download welcome pack PDF
            </a>
          ) : null}
          {run.hasUnviewedCredential ? (
            <Button variant="primary" onClick={() => void revealCredential()}>
              Show one-time credential
            </Button>
          ) : null}
          {run.canRestore ? (
            <Button variant="secondary" onClick={() => void restoreUser()} disabled={busy}>
              <RotateCcw size={16} aria-hidden="true" />
              Restore user
            </Button>
          ) : null}
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}
      {run.statusReason ? <AlertBanner tone="warning">{run.statusReason}</AlertBanner> : null}

      {credential ? (
        <CredentialDisplay
          label={credential.kind}
          value={credential.value}
          note="Shown once and never stored by Rollcall. Copy or hand it to the user now."
        />
      ) : null}

      <ApprovalBanner
        run={run}
        isBusy={busy}
        onApprove={(comment) => void decide('Approved', comment)}
        onReject={(comment) => void decide('Rejected', comment)}
      />

      <JobStepProgress
        steps={run.steps}
        canRetry={run.canRetry}
        isRetrying={busy}
        onRetry={() => void retry()}
      />

      <ChangePreview plan={run.plan} readOnly />
    </div>
  );
}

export function ApprovalsPage() {
  const query = useQuery({
    queryKey: ['approvals'],
    queryFn: () => apiRequest<WorkflowRunDto[]>('/api/approvals'),
  });
  const [error, setError] = useState<string | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  const decide = async (runId: string, decision: 'Approved' | 'Rejected', comment: string) => {
    setError(null);
    setBusyId(runId);
    try {
      await apiRequest(`/api/approvals/${runId}/decision`, {
        method: 'POST',
        body: { decision, comment: comment || null },
      });
      await query.refetch();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Decision failed.');
    } finally {
      setBusyId(null);
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Approvals</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Review frozen change previews before sensitive changes run.
          </p>
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      {(query.data ?? []).length === 0 ? (
        <section className="rc-card">
          <EmptyState
            icon={<Stamp size={48} strokeWidth={1.5} />}
            message="No requests are waiting for approval."
          />
        </section>
      ) : (
        (query.data ?? []).map((run) => (
          <section className="rc-card" key={run.id}>
            <div className="rc-row-between">
              <Link to={`/runs/${run.id}`} className="rc-section-title">
                {run.type}: {run.targetUserDisplayName ?? run.targetUserRef}
              </Link>
              <span className="rc-muted rc-tabular">{formatUiDateTime(run.createdAt)}</span>
            </div>
            <ApprovalBanner
              run={run}
              isBusy={busyId === run.id}
              onApprove={(c) => void decide(run.id, 'Approved', c)}
              onReject={(c) => void decide(run.id, 'Rejected', c)}
            />
            <ChangePreview plan={run.plan} readOnly />
          </section>
        ))
      )}
    </div>
  );
}

export function AuditPage({ isAdmin }: { isAdmin: boolean }) {
  const [actor, setActor] = useState('');
  const [target, setTarget] = useState('');
  const [action, setAction] = useState('');
  const [verifyMsg, setVerifyMsg] = useState<{ ok: boolean; text: string } | null>(null);

  const qs = new URLSearchParams();
  if (actor.trim()) qs.set('actor', actor.trim());
  if (target.trim()) qs.set('target', target.trim());
  if (action.trim()) qs.set('action', action.trim());

  const query = useQuery({
    queryKey: ['audit', qs.toString()],
    queryFn: () => apiRequest<CursorPage<AuditEventDto>>(`/api/audit?${qs.toString()}`),
  });

  const verify = async () => {
    const res = await apiRequest<{ isValid: boolean; message: string }>('/api/audit/verify', {
      method: 'POST',
    });
    setVerifyMsg({ ok: res.isValid, text: res.message });
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Audit log</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Append-only, hash-chained record of every Rollcall change.
          </p>
        </div>
        <div className="rc-row">
          {isAdmin ? (
            <Button variant="secondary" onClick={() => void verify()}>
              <ShieldCheck size={16} aria-hidden="true" />
              Verify audit log
            </Button>
          ) : null}
          <a
            className="rc-btn rc-btn-secondary"
            href={`/api/audit/export?${qs.toString()}`}
            download
          >
            <Download size={16} aria-hidden="true" />
            Export CSV
          </a>
        </div>
      </div>

      {verifyMsg ? (
        <AlertBanner tone={verifyMsg.ok ? 'success' : 'danger'}>{verifyMsg.text}</AlertBanner>
      ) : null}

      <div className="rc-card">
        <div className="rc-grid-4">
          <FormField id="audit-actor" label="Actor" optional>
            <input
              id="audit-actor"
              className="rc-input"
              value={actor}
              onChange={(e) => {
                setActor(e.target.value);
              }}
            />
          </FormField>
          <FormField id="audit-action" label="Action" optional>
            <input
              id="audit-action"
              className="rc-input rc-input-mono"
              value={action}
              onChange={(e) => {
                setAction(e.target.value);
              }}
            />
          </FormField>
          <FormField id="audit-target" label="Target" optional>
            <input
              id="audit-target"
              className="rc-input rc-input-mono"
              value={target}
              onChange={(e) => {
                setTarget(e.target.value);
              }}
            />
          </FormField>
        </div>
      </div>

      <div className="rc-table-wrap">
        {(query.data?.items ?? []).length === 0 ? (
          <EmptyState
            icon={<ScrollText size={48} strokeWidth={1.5} />}
            message="No audit events match your filters."
          />
        ) : (
          <table className="rc-table">
            <thead>
              <tr>
                <th>Time</th>
                <th>Actor</th>
                <th>Action</th>
                <th>Target</th>
                <th>Outcome</th>
                <th>Summary</th>
              </tr>
            </thead>
            <tbody>
              {(query.data?.items ?? []).map((ev) => (
                <tr key={ev.id}>
                  <td>{formatUiDateTime(ev.occurredAt)}</td>
                  <td className="rc-mono">{ev.actorUpn}</td>
                  <td className="rc-mono">{ev.action}</td>
                  <td className="rc-mono">{ev.target}</td>
                  <td>
                    <span className="rc-badge rc-badge-muted">{ev.outcome}</span>
                  </td>
                  <td>
                    {ev.summary ?? '—'}
                    {ev.runId ? (
                      <>
                        {' '}
                        <Link to={`/runs/${ev.runId}`}>View run</Link>
                      </>
                    ) : null}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  );
}
