// SPDX-License-Identifier: GPL-3.0-or-later
import { render, screen, fireEvent } from '@testing-library/react';
import axe from 'axe-core';
import { describe, expect, it, vi } from 'vitest';
import { lintCssContent } from '../scripts/lint-tokens.mjs';
import { ComponentsReferencePage } from './dev/ComponentsReferencePage';
import { ApprovalBanner, DestructiveConfirmDialog } from './components/SignatureComponents';
import { ThemeProvider } from './theme/ThemeProvider';
import type { WorkflowRunDto } from './api/types';

describe('Rollcall design system and accessibility', () => {
  it('CSS token linter rejects Microsoft brand blue #0078D4 and raw literals', () => {
    const violations = lintCssContent(
      '.bad { color: #0078D4; font-size: 14px; box-shadow: 0 2px 4px black; }',
      'test.css',
    );
    expect(violations.some((v) => v.includes('#0078D4'))).toBe(true);
    expect(violations.some((v) => v.includes('font-size'))).toBe(true);
    expect(violations.some((v) => v.includes('box-shadow'))).toBe(true);

    const clean = lintCssContent(
      '.good { color: var(--rc-primary); font-size: var(--rc-text-sm); box-shadow: var(--rc-shadow-overlay); }',
      'good.css',
    );
    expect(clean).toHaveLength(0);
  });

  it('persists Light/Dark/System theme switch on documentElement and localStorage', () => {
    render(
      <ThemeProvider>
        <ComponentsReferencePage />
      </ThemeProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Dark' }));
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(window.localStorage.getItem('rollcall.theme')).toBe('dark');

    fireEvent.click(screen.getByRole('button', { name: 'Light' }));
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
    expect(window.localStorage.getItem('rollcall.theme')).toBe('light');

    fireEvent.click(screen.getByRole('button', { name: 'System' }));
    expect(document.documentElement.getAttribute('data-theme')).toBeNull();
  });

  it('keeps destructive confirmation danger button disabled until exact UPN is typed', () => {
    const onConfirm = vi.fn();
    render(
      <DestructiveConfirmDialog
        open
        onOpenChange={() => undefined}
        title="Offboard Jordan Lee"
        impactSentence="Blocks sign-in immediately."
        expectedText="jordan.lee@contoso.com"
        inputLabel="User principal name"
        confirmButtonLabel="Run offboarding"
        onConfirm={onConfirm}
      />,
    );

    const btn = screen.getByRole('button', { name: 'Run offboarding' });
    expect(btn).toBeDisabled();

    fireEvent.change(screen.getByLabelText('User principal name'), {
      target: { value: 'wrong@contoso.com' },
    });
    expect(btn).toBeDisabled();

    fireEvent.change(screen.getByLabelText('User principal name'), {
      target: { value: 'jordan.lee@contoso.com' },
    });
    expect(btn).not.toBeDisabled();
    fireEvent.click(btn);
    expect(onConfirm).toHaveBeenCalledOnce();
  });

  it('requires a non-empty comment before rejecting in ApprovalBanner', () => {
    const onReject = vi.fn();
    const run: WorkflowRunDto = {
      id: 'run-1',
      type: 'Offboarding',
      status: 'AwaitingApproval',
      requestedByUpn: 'tech@contoso.com',
      requestedByDisplayName: 'Tech',
      targetUserRef: 'jordan.lee@contoso.com',
      targetUserDisplayName: 'Jordan Lee',
      createdAt: '2026-11-03T09:00:00Z',
      scheduledFor: null,
      startedAt: null,
      completedAt: null,
      planHash: 'abc123',
      plan: {
        actionableChangeCount: 2,
        noOpCount: 0,
        systemCount: 1,
        approvalRequiredCount: 2,
        systems: ['EntraId'],
        warnings: [],
        blockingErrors: [],
        planHash: 'abc123',
        steps: [],
      },
      steps: [],
      approvals: [],
      roleTemplateId: null,
      roleTemplateVersion: null,
      canApprove: true,
      canRetry: false,
      canRestore: false,
      hasUnviewedCredential: false,
      statusReason: null,
    };

    render(<ApprovalBanner run={run} onApprove={() => undefined} onReject={onReject} />);
    fireEvent.click(screen.getByRole('button', { name: 'Reject request' }));
    expect(onReject).not.toHaveBeenCalled();
    expect(screen.getByRole('alert')).toHaveTextContent('Enter a reason before rejecting');

    fireEvent.change(screen.getByLabelText('Review comment'), {
      target: { value: 'Leave date moved to Friday.' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Reject request' }));
    expect(onReject).toHaveBeenCalledWith('Leave date moved to Friday.');
  });

  it('component reference page passes axe-core with zero serious or critical violations', async () => {
    const { container } = render(
      <ThemeProvider>
        <ComponentsReferencePage />
      </ThemeProvider>,
    );
    const results = await axe.run(container);
    const severe = results.violations.filter(
      (v) => v.impact === 'serious' || v.impact === 'critical',
    );
    expect(severe).toEqual([]);
  });
});
