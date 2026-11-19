// SPDX-License-Identifier: GPL-3.0-or-later
import type { WorkflowRunStatus, WorkflowStepStatus } from '../api/types';

export type BrandStatus =
  | 'succeeded'
  | 'failed'
  | 'needs-attention'
  | 'running'
  | 'queued'
  | 'scheduled'
  | 'awaiting-approval'
  | 'paused';

export function mapRunStatusToBrand(status: WorkflowRunStatus): {
  kind: BrandStatus;
  label: string;
} {
  switch (status) {
    case 'Succeeded':
      return { kind: 'succeeded', label: 'Succeeded' };
    case 'Running':
      return { kind: 'running', label: 'Running' };
    case 'WaitingForAgent':
      return { kind: 'running', label: 'Waiting for AD agent' };
    case 'Scheduled':
      return { kind: 'scheduled', label: 'Scheduled' };
    case 'AwaitingApproval':
      return { kind: 'awaiting-approval', label: 'Awaiting approval' };
    case 'NeedsReview':
      return { kind: 'needs-attention', label: 'Needs attention' };
    case 'PartiallyFailed':
      return { kind: 'failed', label: 'Failed' };
    case 'Rejected':
      return { kind: 'failed', label: 'Rejected' };
    case 'Cancelled':
      return { kind: 'paused', label: 'Cancelled' };
    case 'Draft':
    case 'Planned':
    case 'Queued':
    default:
      return { kind: 'queued', label: 'Queued' };
  }
}

export function mapStepStatusToBrand(status: WorkflowStepStatus): {
  kind: BrandStatus;
  label: string;
} {
  switch (status) {
    case 'Succeeded':
      return { kind: 'succeeded', label: 'Succeeded' };
    case 'Failed':
      return { kind: 'failed', label: 'Failed' };
    case 'Running':
      return { kind: 'running', label: 'Running' };
    case 'Waiting':
      return { kind: 'running', label: 'Waiting' };
    case 'Skipped':
      return { kind: 'paused', label: 'Skipped' };
    case 'Pending':
    default:
      return { kind: 'queued', label: 'Queued' };
  }
}
