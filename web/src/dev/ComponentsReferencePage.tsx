// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { ClipboardList } from 'lucide-react';
import type { PlanSummaryDto, WorkflowRunDto, WorkflowStepDto } from '../api/types';
import {
  AlertBanner,
  Button,
  CredentialDisplay,
  EmptyState,
  FormField,
  StatusBadge,
} from '../components/Primitives';
import {
  ApprovalBanner,
  ChangePreview,
  DestructiveConfirmDialog,
  JobStepProgress,
} from '../components/SignatureComponents';
import { useTheme } from '../theme/themeState';

const SAMPLE_PLAN: PlanSummaryDto = {
  actionableChangeCount: 3,
  noOpCount: 1,
  systemCount: 2,
  approvalRequiredCount: 1,
  systems: ['EntraId', 'ExchangeOnline'],
  warnings: ['Mailbox is 52.4 GB, exceeding the 50 GB limit for an unlicensed shared mailbox.'],
  blockingErrors: [],
  planHash: 'a1b2c3d4e5f6',
  steps: [
    {
      sequence: 1,
      stepKind: 'graph.group.addMember',
      targetSystem: 'EntraId',
      area: 'Groups',
      operation: 'Add',
      description: 'Add Jordan Lee to group Sales – All Staff',
      objectName: 'sg-sales-all',
      oldValue: null,
      newValue: null,
      isNoOp: false,
      noOpReason: null,
      requiresApproval: false,
      warnings: [],
    },
    {
      sequence: 2,
      stepKind: 'graph.user.updateAttributes',
      targetSystem: 'EntraId',
      area: 'Attributes',
      operation: 'Change',
      description: 'Set Department on Jordan Lee',
      objectName: 'jordan.lee@contoso.com',
      oldValue: 'Sales',
      newValue: 'Finance',
      isNoOp: false,
      noOpReason: null,
      requiresApproval: false,
      warnings: [],
    },
    {
      sequence: 3,
      stepKind: 'graph.license.remove',
      targetSystem: 'EntraId',
      area: 'Licenses',
      operation: 'Remove',
      description: 'Remove license Microsoft 365 E3',
      objectName: 'SPE_E3',
      oldValue: null,
      newValue: null,
      isNoOp: false,
      noOpReason: null,
      requiresApproval: true,
      warnings: [],
    },
    {
      sequence: 4,
      stepKind: 'exchange.mailbox.setHiddenFromGal',
      targetSystem: 'ExchangeOnline',
      area: 'Mailbox',
      operation: 'NoOp',
      description: 'Hide jordan.lee@contoso.com from address lists',
      objectName: 'jordan.lee@contoso.com',
      oldValue: null,
      newValue: null,
      isNoOp: true,
      noOpReason: 'Already hidden from address lists',
      requiresApproval: false,
      warnings: [],
    },
  ],
};

const SAMPLE_STEPS: WorkflowStepDto[] = [
  {
    id: 'step-1',
    sequence: 1,
    kind: 'graph.user.setAccountEnabled',
    targetSystem: 'EntraId',
    area: 'Account',
    description: 'Block sign-in for Jordan Lee',
    objectName: 'jordan.lee@contoso.com',
    status: 'Succeeded',
    attempts: 1,
    plainLanguageError: null,
    rawError: null,
    graphRequestId: null,
    graphClientRequestId: null,
    startedAt: '2026-11-03T09:00:00Z',
    completedAt: '2026-11-03T09:00:01Z',
    durationMs: 1100,
    hasUndoData: true,
  },
  {
    id: 'step-2',
    sequence: 2,
    kind: 'exchange.mailbox.convertToShared',
    targetSystem: 'ExchangeOnline',
    area: 'Mailbox',
    description: 'Convert mailbox jordan.lee@contoso.com to a shared mailbox',
    objectName: 'jordan.lee@contoso.com',
    status: 'Failed',
    attempts: 1,
    plainLanguageError:
      "Couldn't convert the mailbox to shared. Exchange Online reported that the mailbox is on litigation hold. Remove the hold or skip this step, then retry.",
    rawError: 'ExchangeOnline: Mailbox is on LitigationHoldEnabled=True',
    graphRequestId: 'req-1234',
    graphClientRequestId: 'cli-5678',
    startedAt: '2026-11-03T09:00:02Z',
    completedAt: null,
    durationMs: null,
    hasUndoData: false,
  },
];

const SAMPLE_APPROVER_RUN: WorkflowRunDto = {
  id: 'run-demo-1',
  type: 'Offboarding',
  status: 'AwaitingApproval',
  requestedByUpn: 'tech@contoso.com',
  requestedByDisplayName: 'Sam Technician',
  targetUserRef: 'jordan.lee@contoso.com',
  targetUserDisplayName: 'Jordan Lee',
  createdAt: '2026-11-03T09:00:00Z',
  scheduledFor: null,
  startedAt: null,
  completedAt: null,
  planHash: 'a1b2c3d4e5f6',
  plan: SAMPLE_PLAN,
  steps: SAMPLE_STEPS,
  approvals: [],
  roleTemplateId: null,
  roleTemplateVersion: null,
  canApprove: true,
  canRetry: true,
  canRestore: false,
  hasUnviewedCredential: false,
  statusReason: null,
};

export function ComponentsReferencePage() {
  const { theme, setTheme } = useTheme();
  const [confirmOpen, setConfirmOpen] = useState(false);

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Rollcall component reference</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Every brand-guide §8 component in every state, rendered strictly from
            docs/brand/tokens.css.
          </p>
        </div>
        <div className="rc-row">
          <Button
            variant={theme === 'light' ? 'primary' : 'secondary'}
            compact
            onClick={() => {
              setTheme('light');
            }}
          >
            Light
          </Button>
          <Button
            variant={theme === 'dark' ? 'primary' : 'secondary'}
            compact
            onClick={() => {
              setTheme('dark');
            }}
          >
            Dark
          </Button>
          <Button
            variant={theme === 'system' ? 'primary' : 'secondary'}
            compact
            onClick={() => {
              setTheme('system');
            }}
          >
            System
          </Button>
        </div>
      </div>

      <section className="rc-card">
        <h2 className="rc-section-title">Buttons and status badges</h2>
        <div className="rc-row">
          <Button variant="primary">Run 3 changes</Button>
          <Button variant="secondary">Export CSV</Button>
          <Button variant="ghost">View details</Button>
          <Button
            variant="danger"
            onClick={() => {
              setConfirmOpen(true);
            }}
          >
            Run offboarding
          </Button>
        </div>
        <div className="rc-row">
          <StatusBadge kind="succeeded" label="Succeeded" />
          <StatusBadge kind="failed" label="Failed" />
          <StatusBadge kind="needs-attention" label="Needs attention" />
          <StatusBadge kind="running" label="Running" />
          <StatusBadge kind="queued" label="Queued" />
          <StatusBadge kind="scheduled" label="Scheduled" />
          <StatusBadge kind="awaiting-approval" label="Awaiting approval" />
          <StatusBadge kind="paused" label="Skipped" />
        </div>
      </section>

      <section className="rc-card">
        <h2 className="rc-section-title">Form fields, alerts, and credential display</h2>
        <div className="rc-grid-2">
          <FormField
            id="ref-upn"
            label="User principal name"
            helper="Shown in Atkinson Hyperlegible Mono."
          >
            <input
              id="ref-upn"
              className="rc-input rc-input-mono"
              defaultValue="jordan.lee@contoso.com"
            />
          </FormField>
          <FormField id="ref-err" label="Job title" optional error="Enter a value before saving.">
            <input id="ref-err" className="rc-input" />
          </FormField>
        </div>
        <AlertBanner tone="warning" title="Certificate expiring soon">
          The app certificate expires in 21 days. Rotate it now to avoid an outage.
        </AlertBanner>
        <CredentialDisplay
          label="Temporary Access Pass"
          value="Kx7m Qp2R 9tVw"
          note="Valid for 60 minutes, single use. Shown once."
        />
      </section>

      <ChangePreview plan={SAMPLE_PLAN} onConfirm={() => undefined} />

      <ApprovalBanner
        run={SAMPLE_APPROVER_RUN}
        onApprove={() => undefined}
        onReject={() => undefined}
      />

      <ApprovalBanner
        run={{ ...SAMPLE_APPROVER_RUN, id: 'run-demo-2', canApprove: false }}
        onApprove={() => undefined}
        onReject={() => undefined}
      />

      <JobStepProgress steps={SAMPLE_STEPS} canRetry onRetry={() => undefined} />

      <section className="rc-card">
        <EmptyState
          icon={<ClipboardList size={48} strokeWidth={1.5} />}
          message="No role templates yet. Templates define what a job gets: groups, licenses, and policies."
          action={<Button variant="primary">Create template</Button>}
        />
      </section>

      <DestructiveConfirmDialog
        open={confirmOpen}
        onOpenChange={setConfirmOpen}
        title="Offboard Jordan Lee"
        impactSentence="This blocks sign-in for Jordan Lee immediately and signs them out of every session. Type their UPN to confirm."
        expectedText="jordan.lee@contoso.com"
        inputLabel="User principal name"
        confirmButtonLabel="Run offboarding"
        onConfirm={() => {
          setConfirmOpen(false);
        }}
      />
    </div>
  );
}
