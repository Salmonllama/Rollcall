// SPDX-License-Identifier: GPL-3.0-or-later
import * as Dialog from '@radix-ui/react-dialog';
import { useState } from 'react';
import { ChevronDown, ChevronRight, Hourglass, RotateCcw, TriangleAlert } from 'lucide-react';
import { formatUiDateTime } from '../api/client';
import type {
  PlanSummaryDto,
  StepPlanDto,
  TargetSystem,
  WorkflowRunDto,
  WorkflowStepDto,
} from '../api/types';
import { AlertBanner, Button, FormField, StatusBadge } from './Primitives';
import { mapStepStatusToBrand } from './statusMapping';

const SYSTEM_LABELS: Record<TargetSystem, string> = {
  EntraId: 'Microsoft Entra ID',
  ExchangeOnline: 'Exchange Online',
  ActiveDirectory: 'On-prem Active Directory',
  OneDrive: 'OneDrive',
  Portal: 'Rollcall',
};

function opMarker(step: StepPlanDto): { symbol: string; className: string } {
  if (step.isNoOp) return { symbol: '·', className: 'rc-op-marker rc-op-noop' };
  switch (step.operation) {
    case 'Add':
      return { symbol: '+', className: 'rc-op-marker rc-op-add' };
    case 'Remove':
      return { symbol: '−', className: 'rc-op-marker rc-op-remove' };
    case 'Change':
    case 'Info':
    default:
      return { symbol: '~', className: 'rc-op-marker rc-op-change' };
  }
}

/**
 * Signature component (brand-guide.md §8): renders any list of StepPlans grouped by target system
 * then by area, with Mono +/-/~ markers, old -> new diffs, muted no-ops, approval badges, summary bar,
 * and count-bearing primary action button.
 */
export function ChangePreview({
  plan,
  primaryActionLabel,
  onConfirm,
  isSubmitting = false,
  secondaryAction,
  readOnly = false,
}: {
  plan: PlanSummaryDto;
  primaryActionLabel?: string;
  onConfirm?: () => void;
  isSubmitting?: boolean;
  secondaryAction?: React.ReactNode;
  readOnly?: boolean;
}) {
  const [showAll, setShowAll] = useState(plan.steps.length <= 20);
  const visibleSteps = showAll ? plan.steps : plan.steps.slice(0, 10);

  const grouped = new Map<TargetSystem, Map<string, StepPlanDto[]>>();
  for (const step of visibleSteps) {
    let areaMap = grouped.get(step.targetSystem);
    if (!areaMap) {
      areaMap = new Map<string, StepPlanDto[]>();
      grouped.set(step.targetSystem, areaMap);
    }
    const list = areaMap.get(step.area) ?? [];
    list.push(step);
    areaMap.set(step.area, list);
  }

  const changeWord = plan.actionableChangeCount === 1 ? 'change' : 'changes';
  const systemWord = plan.systemCount === 1 ? 'system' : 'systems';
  const summaryParts = [
    `${String(plan.actionableChangeCount)} ${changeWord} across ${String(plan.systemCount)} ${systemWord}`,
  ];
  if (plan.approvalRequiredCount > 0) {
    summaryParts.push(`${String(plan.approvalRequiredCount)} need approval`);
  }
  if (plan.noOpCount > 0) {
    summaryParts.push(`${String(plan.noOpCount)} already up to date`);
  }

  const buttonLabel =
    primaryActionLabel ??
    (plan.actionableChangeCount === 0
      ? 'No changes to run'
      : plan.approvalRequiredCount > 0
        ? `Request approval for ${String(plan.actionableChangeCount)} ${changeWord}`
        : `Run ${String(plan.actionableChangeCount)} ${changeWord}`);

  return (
    <section className="rc-preview" aria-label="Change preview">
      <div className="rc-preview-summary">
        <span className="rc-tabular">{summaryParts.join(' · ')}</span>
        <span className="rc-mono rc-muted">Plan {plan.planHash.slice(0, 10)}</span>
      </div>

      {plan.blockingErrors.length > 0 ? (
        <div className="rc-preview-group">
          <AlertBanner tone="danger" title="Fix these issues before continuing">
            <ul style={{ margin: 0, paddingLeft: '20px' }}>
              {plan.blockingErrors.map((e) => (
                <li key={e}>{e}</li>
              ))}
            </ul>
          </AlertBanner>
        </div>
      ) : null}

      {plan.warnings.length > 0 ? (
        <div className="rc-preview-group">
          <AlertBanner tone="warning" title="Review before running">
            <ul style={{ margin: 0, paddingLeft: '20px' }}>
              {plan.warnings.map((w) => (
                <li key={w}>{w}</li>
              ))}
            </ul>
          </AlertBanner>
        </div>
      ) : null}

      {[...grouped.entries()].map(([system, areas]) => (
        <div className="rc-preview-group" key={system}>
          <div className="rc-eyebrow">{SYSTEM_LABELS[system]}</div>
          {[...areas.entries()].map(([area, steps]) => (
            <div className="rc-stack" key={`${system}-${area}`}>
              <strong style={{ fontSize: 'var(--rc-text-sm)' }}>{area}</strong>
              {steps.map((step) => {
                const marker = opMarker(step);
                return (
                  <div
                    key={step.sequence}
                    className={`rc-preview-line ${step.isNoOp ? 'rc-preview-line-noop' : ''}`}
                  >
                    <span className={marker.className} aria-hidden="true">
                      {marker.symbol}
                    </span>
                    <div>
                      <span>{step.description}</span>
                      {step.objectName ? (
                        <span className="rc-mono rc-muted"> ({step.objectName})</span>
                      ) : null}
                      {step.oldValue !== null || step.newValue !== null ? (
                        <span className="rc-diff-values">
                          {step.oldValue ?? '(none)'} → {step.newValue ?? '(none)'}
                        </span>
                      ) : null}
                      {step.isNoOp && step.noOpReason ? (
                        <span className="rc-muted"> — {step.noOpReason}</span>
                      ) : null}
                    </div>
                    <div>
                      {step.requiresApproval && !step.isNoOp ? (
                        <span className="rc-badge rc-badge-info">
                          <Hourglass size={14} strokeWidth={1.75} aria-hidden="true" />
                          Approval required
                        </span>
                      ) : null}
                    </div>
                  </div>
                );
              })}
            </div>
          ))}
        </div>
      ))}

      {plan.steps.length > 20 ? (
        <div className="rc-preview-group">
          <Button
            variant="ghost"
            compact
            onClick={() => {
              setShowAll((v) => !v);
            }}
          >
            {showAll ? 'Collapse preview' : `Show all ${String(plan.steps.length)} planned changes`}
          </Button>
        </div>
      ) : null}

      {!readOnly && onConfirm ? (
        <div className="rc-preview-footer">
          {secondaryAction}
          <Button
            variant="primary"
            onClick={onConfirm}
            disabled={
              isSubmitting || plan.blockingErrors.length > 0 || plan.actionableChangeCount === 0
            }
          >
            {isSubmitting ? 'Submitting…' : buttonLabel}
          </Button>
        </div>
      ) : null}
    </section>
  );
}

/**
 * Signature component (brand-guide.md §8): vertical step list with status icon + word,
 * target system, duration, collapsible raw error Mono block, Retry button, and polite live region.
 */
export function JobStepProgress({
  steps,
  canRetry,
  onRetry,
  isRetrying = false,
}: {
  steps: WorkflowStepDto[];
  canRetry: boolean;
  onRetry?: () => void;
  isRetrying?: boolean;
}) {
  const [openRaw, setOpenRaw] = useState<Record<string, boolean>>({});
  const completedCount = steps.filter(
    (s) => s.status === 'Succeeded' || s.status === 'Skipped',
  ).length;
  const failedCount = steps.filter((s) => s.status === 'Failed').length;

  return (
    <section className="rc-stack" aria-label="Step progress">
      <div className="rc-sr-only" aria-live="polite">
        {`${String(completedCount)} of ${String(steps.length)} steps finished${
          failedCount > 0 ? `, ${String(failedCount)} failed` : ''
        }.`}
      </div>
      <ol className="rc-step-list">
        {steps.map((step) => {
          const brand = mapStepStatusToBrand(step.status);
          const isFailed = step.status === 'Failed';
          const showRaw = Boolean(openRaw[step.id]);
          return (
            <li key={step.id} className={`rc-step-item ${isFailed ? 'rc-step-item-failed' : ''}`}>
              <div className="rc-row-between">
                <div className="rc-row">
                  <StatusBadge kind={brand.kind} label={brand.label} />
                  <strong>{step.description}</strong>
                  <span className="rc-mono rc-muted">{SYSTEM_LABELS[step.targetSystem]}</span>
                </div>
                <div className="rc-row rc-muted rc-tabular">
                  {step.attempts > 1 ? <span>{String(step.attempts)} attempts</span> : null}
                  {step.durationMs !== null ? (
                    <span>{(step.durationMs / 1000).toFixed(1)} s</span>
                  ) : null}
                </div>
              </div>

              {isFailed && step.plainLanguageError ? (
                <div className="rc-stack">
                  <p style={{ margin: 0, color: 'var(--rc-danger)' }}>{step.plainLanguageError}</p>
                  <div className="rc-row">
                    {canRetry && onRetry ? (
                      <Button variant="primary" compact onClick={onRetry} disabled={isRetrying}>
                        <RotateCcw size={16} aria-hidden="true" />
                        {isRetrying ? 'Retrying…' : 'Retry step'}
                      </Button>
                    ) : null}
                    {step.rawError ? (
                      <Button
                        variant="ghost"
                        compact
                        onClick={() => {
                          setOpenRaw((prev) => ({ ...prev, [step.id]: !prev[step.id] }));
                        }}
                      >
                        {showRaw ? (
                          <ChevronDown size={16} aria-hidden="true" />
                        ) : (
                          <ChevronRight size={16} aria-hidden="true" />
                        )}
                        {showRaw ? 'Hide raw error' : 'Show raw error'}
                      </Button>
                    ) : null}
                  </div>
                  {showRaw && step.rawError ? (
                    <pre className="rc-code-block">
                      {step.rawError}
                      {step.graphRequestId ? `\nrequest-id: ${step.graphRequestId}` : ''}
                      {step.graphClientRequestId
                        ? `\nclient-request-id: ${step.graphClientRequestId}`
                        : ''}
                    </pre>
                  ) : null}
                </div>
              ) : null}
            </li>
          );
        })}
      </ol>
    </section>
  );
}

/**
 * Signature component (brand-guide.md §8): full-width approval banner working down to 320px width.
 */
export function ApprovalBanner({
  run,
  onApprove,
  onReject,
  isBusy = false,
}: {
  run: WorkflowRunDto;
  onApprove: (comment: string) => void;
  onReject: (comment: string) => void;
  isBusy?: boolean;
}) {
  const [comment, setComment] = useState('');
  const [error, setError] = useState<string | null>(null);

  if (run.status !== 'AwaitingApproval') return null;

  const handleReject = () => {
    if (!comment.trim()) {
      setError('Enter a reason before rejecting this request.');
      return;
    }
    setError(null);
    onReject(comment.trim());
  };

  return (
    <section className="rc-banner rc-banner-info" aria-label="Approval required">
      <div className="rc-stack" style={{ width: '100%' }}>
        <div className="rc-row-between">
          <div className="rc-row">
            <StatusBadge kind="awaiting-approval" label="Awaiting approval" />
            <strong>
              Requested by {run.requestedByDisplayName} ({run.requestedByUpn})
            </strong>
          </div>
          <span className="rc-muted rc-tabular">{formatUiDateTime(run.createdAt)}</span>
        </div>

        {run.canApprove ? (
          <div className="rc-stack">
            <FormField
              id={`approval-comment-${run.id}`}
              label="Review comment"
              helper="Optional when approving; required when rejecting."
              error={error ?? undefined}
            >
              <textarea
                id={`approval-comment-${run.id}`}
                className="rc-textarea"
                value={comment}
                onChange={(e) => {
                  setComment(e.target.value);
                  if (error) setError(null);
                }}
                placeholder="Add context for the audit log"
              />
            </FormField>
            <div className="rc-row">
              <Button
                variant="primary"
                onClick={() => {
                  onApprove(comment.trim());
                }}
                disabled={isBusy}
              >
                Approve {run.plan.actionableChangeCount} changes
              </Button>
              <Button variant="danger" onClick={handleReject} disabled={isBusy}>
                Reject request
              </Button>
            </div>
          </div>
        ) : (
          <p className="rc-muted" style={{ margin: 0 }}>
            Waiting for an approver to review this request. Requesters can&apos;t approve their own
            changes.
          </p>
        )}
      </div>
    </section>
  );
}

/**
 * Signature component (brand-guide.md §8): Radix Dialog requiring exact UPN or bulk count entry
 * before enabling the Danger confirmation button.
 */
export function DestructiveConfirmDialog({
  open,
  onOpenChange,
  title,
  impactSentence,
  expectedText,
  inputLabel,
  confirmButtonLabel,
  onConfirm,
  isBusy = false,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  impactSentence: string;
  expectedText: string;
  inputLabel: string;
  confirmButtonLabel: string;
  onConfirm: () => void;
  isBusy?: boolean;
}) {
  const [typed, setTyped] = useState('');
  const matches = typed.trim().toLowerCase() === expectedText.trim().toLowerCase();

  return (
    <Dialog.Root
      open={open}
      onOpenChange={(next) => {
        if (!next) setTyped('');
        onOpenChange(next);
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="rc-dialog-overlay" />
        <Dialog.Content className="rc-dialog-content">
          <div className="rc-row">
            <TriangleAlert size={20} color="var(--rc-danger)" aria-hidden="true" />
            <Dialog.Title className="rc-section-title">{title}</Dialog.Title>
          </div>
          <Dialog.Description style={{ margin: 0 }}>{impactSentence}</Dialog.Description>
          <FormField
            id="destructive-confirm-input"
            label={inputLabel}
            helper={`Type ${expectedText} exactly to enable confirmation.`}
          >
            <input
              id="destructive-confirm-input"
              className="rc-input rc-input-mono"
              value={typed}
              onChange={(e) => {
                setTyped(e.target.value);
              }}
              placeholder={expectedText}
              autoComplete="off"
            />
          </FormField>
          <div className="rc-row" style={{ justifyContent: 'flex-end' }}>
            <Dialog.Close asChild>
              <Button variant="secondary">Cancel</Button>
            </Dialog.Close>
            <Button
              variant="danger"
              disabled={!matches || isBusy}
              onClick={() => {
                onConfirm();
                setTyped('');
              }}
            >
              {isBusy ? 'Working…' : confirmButtonLabel}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
