// SPDX-License-Identifier: GPL-3.0-or-later
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { CalendarClock, RefreshCw, Server, Stamp, Ticket, TriangleAlert } from 'lucide-react';
import { apiRequest, formatUiDate } from '../../api/client';
import type { DashboardSummaryDto } from '../../api/types';
import { Button, EmptyState, StatusBadge } from '../../components/Primitives';
import { mapRunStatusToBrand } from '../../components/statusMapping';

export function DashboardPage() {
  const query = useQuery({
    queryKey: ['dashboard'],
    queryFn: () => apiRequest<DashboardSummaryDto>('/api/dashboard'),
  });

  const data = query.data;

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Dashboard</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Upcoming joiners and leavers, pending approvals, license health, and recent failures.
          </p>
        </div>
        <Button
          variant="secondary"
          onClick={() => void apiRequest('/api/dashboard?refresh=true').then(() => query.refetch())}
        >
          <RefreshCw size={16} aria-hidden="true" />
          Refresh
        </Button>
      </div>

      {data ? (
        <>
          <div className="rc-grid-4">
            <Link to="/runs?status=Scheduled&type=Onboarding" className="rc-card">
              <span className="rc-eyebrow">Upcoming onboardings</span>
              <span className="rc-figure">{data.upcomingOnboardingsCount.toLocaleString()}</span>
            </Link>
            <Link to="/runs?status=Scheduled&type=Offboarding" className="rc-card">
              <span className="rc-eyebrow">Upcoming offboardings</span>
              <span className="rc-figure">{data.upcomingOffboardingsCount.toLocaleString()}</span>
            </Link>
            <Link to="/approvals" className="rc-card">
              <span className="rc-eyebrow">Pending approvals</span>
              <span className="rc-figure">{data.pendingApprovalsCount.toLocaleString()}</span>
            </Link>
            <Link to="/runs?status=PartiallyFailed" className="rc-card">
              <span className="rc-eyebrow">Recent failures</span>
              <span className="rc-figure">{data.recentFailuresCount.toLocaleString()}</span>
            </Link>
            <Link to="/licenses" className="rc-card">
              <span className="rc-eyebrow">Available license seats</span>
              <span className="rc-figure">{data.availableLicenseSeats.toLocaleString()}</span>
            </Link>
            <Link to="/licenses" className="rc-card">
              <span className="rc-eyebrow">Licenses on disabled accounts</span>
              <span className="rc-figure">
                {data.licensedDisabledAccountsCount.toLocaleString()}
              </span>
            </Link>
            <Link to="/admin/agents" className="rc-card">
              <span className="rc-eyebrow">AD agents connected</span>
              <div className="rc-row">
                <Server size={20} aria-hidden="true" />
                <span className="rc-figure">{data.connectedAgentsCount}</span>
                {data.offlineAgentsCount > 0 ? (
                  <StatusBadge kind="failed" label={`${String(data.offlineAgentsCount)} offline`} />
                ) : null}
              </div>
            </Link>
          </div>

          <div className="rc-grid-2">
            <section className="rc-card">
              <div className="rc-row-between">
                <h2 className="rc-section-title">Upcoming onboardings</h2>
                <Link to="/onboard" className="rc-btn rc-btn-ghost rc-btn-compact">
                  Start onboarding
                </Link>
              </div>
              {data.upcomingOnboardings.length === 0 ? (
                <EmptyState
                  icon={<CalendarClock size={48} strokeWidth={1.5} />}
                  message="No one is starting this week."
                />
              ) : (
                <div className="rc-stack">
                  {data.upcomingOnboardings.map((r) => (
                    <Link key={r.id} to={`/runs/${r.id}`} className="rc-row-between">
                      <span>{r.targetUserDisplayName ?? r.targetUserRef}</span>
                      <span className="rc-muted rc-tabular">{formatUiDate(r.scheduledFor)}</span>
                    </Link>
                  ))}
                </div>
              )}
            </section>

            <section className="rc-card">
              <div className="rc-row-between">
                <h2 className="rc-section-title">Pending approvals</h2>
                <Link to="/approvals" className="rc-btn rc-btn-ghost rc-btn-compact">
                  Open inbox
                </Link>
              </div>
              {data.pendingApprovals.length === 0 ? (
                <EmptyState
                  icon={<Stamp size={48} strokeWidth={1.5} />}
                  message="No requests are waiting for approval."
                />
              ) : (
                <div className="rc-stack">
                  {data.pendingApprovals.map((r) => {
                    const b = mapRunStatusToBrand(r.status);
                    return (
                      <Link key={r.id} to={`/runs/${r.id}`} className="rc-row-between">
                        <span>
                          {r.type} — {r.targetUserDisplayName ?? r.targetUserRef}
                        </span>
                        <StatusBadge kind={b.kind} label={b.label} />
                      </Link>
                    );
                  })}
                </div>
              )}
            </section>

            <section className="rc-card">
              <div className="rc-row-between">
                <h2 className="rc-section-title">Recent failures</h2>
                <Link to="/runs" className="rc-btn rc-btn-ghost rc-btn-compact">
                  View all runs
                </Link>
              </div>
              {data.recentFailures.length === 0 ? (
                <EmptyState
                  icon={<TriangleAlert size={48} strokeWidth={1.5} />}
                  message="No failed runs need attention."
                />
              ) : (
                <div className="rc-stack">
                  {data.recentFailures.map((r) => {
                    const b = mapRunStatusToBrand(r.status);
                    return (
                      <Link key={r.id} to={`/runs/${r.id}`} className="rc-row-between">
                        <span>
                          {r.type} — {r.targetUserDisplayName ?? r.targetUserRef}
                        </span>
                        <StatusBadge kind={b.kind} label={b.label} />
                      </Link>
                    );
                  })}
                </div>
              )}
            </section>

            <section className="rc-card">
              <div className="rc-row-between">
                <h2 className="rc-section-title">License watch</h2>
                <Link to="/licenses" className="rc-btn rc-btn-ghost rc-btn-compact">
                  Manage licenses
                </Link>
              </div>
              <EmptyState
                icon={<Ticket size={48} strokeWidth={1.5} />}
                message={`${String(data.availableLicenseSeats)} seats available · ${String(data.licensedDisabledAccountsCount)} disabled account(s) still hold licenses.`}
              />
            </section>
          </div>
        </>
      ) : (
        <p className="rc-muted">Loading dashboard…</p>
      )}
    </div>
  );
}
