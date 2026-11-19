// SPDX-License-Identifier: GPL-3.0-or-later
import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { useNavigate, useParams } from 'react-router-dom';
import { apiRequest, ApiError } from '../../api/client';
import type {
  DirectoryUserDto,
  PlanSummaryDto,
  RoleTemplateDto,
  WorkflowRunDto,
} from '../../api/types';
import { AlertBanner, Button, FormField } from '../../components/Primitives';
import { ChangePreview, DestructiveConfirmDialog } from '../../components/SignatureComponents';

interface OnboardingPlanResponse {
  plan: PlanSummaryDto;
  collisions: {
    field: string;
    proposedValue: string;
    conflictingObjectDisplayName: string;
    conflictingObjectIdentifier: string;
  }[];
  run: WorkflowRunDto | null;
}

export function OnboardingPage() {
  const navigate = useNavigate();
  const templatesQuery = useQuery({
    queryKey: ['templates'],
    queryFn: () => apiRequest<RoleTemplateDto[]>('/api/templates'),
  });

  const [templateId, setTemplateId] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [customUpn, setCustomUpn] = useState('');
  const [customDisplayName, setCustomDisplayName] = useState('');
  const [department, setDepartment] = useState('');
  const [jobTitle, setJobTitle] = useState('');
  const [startDate, setStartDate] = useState('');
  const [planResp, setPlanResp] = useState<OnboardingPlanResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const selectedTemplateId = templateId || templatesQuery.data?.[0]?.id || '';

  const buildPayload = (submit: boolean) => ({
    roleTemplateId: selectedTemplateId,
    firstName: firstName.trim(),
    lastName: lastName.trim(),
    customUpn: customUpn.trim() || null,
    customMailNickname: null,
    customDisplayName: customDisplayName.trim() || null,
    customSamAccountName: null,
    department: department.trim() || null,
    jobTitle: jobTitle.trim() || null,
    managerId: null,
    officeLocation: null,
    usageLocation: null,
    startDate: startDate ? new Date(startDate).toISOString() : null,
    targetOuOverride: null,
    submit,
  });

  const handlePlan = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<OnboardingPlanResponse>('/api/onboarding', {
        method: 'POST',
        body: buildPayload(false),
      });
      setPlanResp(res);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not build onboarding preview.');
    } finally {
      setBusy(false);
    }
  };

  const handleSubmit = async () => {
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<OnboardingPlanResponse>('/api/onboarding', {
        method: 'POST',
        body: buildPayload(true),
      });
      if (res.run) {
        void navigate(`/runs/${res.run.id}`);
      } else {
        setPlanResp(res);
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Onboarding submission failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Onboard user</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Apply a role template, review every change, and schedule for the start date if needed.
          </p>
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      <form className="rc-card" onSubmit={(e) => void handlePlan(e)}>
        <FormField id="ob-template" label="Role template">
          <select
            id="ob-template"
            className="rc-select"
            value={selectedTemplateId}
            onChange={(e) => {
              setTemplateId(e.target.value);
            }}
            required
          >
            {(templatesQuery.data ?? []).map((t) => (
              <option key={t.id} value={t.id}>
                {t.name} (v{String(t.version)})
              </option>
            ))}
          </select>
        </FormField>

        <div className="rc-grid-2">
          <FormField id="ob-first" label="First name">
            <input
              id="ob-first"
              className="rc-input"
              value={firstName}
              onChange={(e) => {
                setFirstName(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="ob-last" label="Last name">
            <input
              id="ob-last"
              className="rc-input"
              value={lastName}
              onChange={(e) => {
                setLastName(e.target.value);
              }}
              required
            />
          </FormField>
          <FormField id="ob-dept" label="Department override" optional>
            <input
              id="ob-dept"
              className="rc-input"
              value={department}
              onChange={(e) => {
                setDepartment(e.target.value);
              }}
            />
          </FormField>
          <FormField id="ob-title" label="Job title override" optional>
            <input
              id="ob-title"
              className="rc-input"
              value={jobTitle}
              onChange={(e) => {
                setJobTitle(e.target.value);
              }}
            />
          </FormField>
          <FormField
            id="ob-start"
            label="Start date"
            optional
            helper="Leave empty to activate immediately."
          >
            <input
              id="ob-start"
              type="datetime-local"
              className="rc-input"
              value={startDate}
              onChange={(e) => {
                setStartDate(e.target.value);
              }}
            />
          </FormField>
        </div>

        {planResp && planResp.collisions.length > 0 ? (
          <AlertBanner tone="danger" title="Naming collision found">
            <p style={{ margin: '0 0 var(--rc-space-2) 0' }}>
              Rollcall never invents alternative usernames. Enter unique values below and re-check:
            </p>
            <div className="rc-grid-2">
              <FormField id="ob-custom-upn" label="Unique UPN">
                <input
                  id="ob-custom-upn"
                  className="rc-input rc-input-mono"
                  value={customUpn}
                  onChange={(e) => {
                    setCustomUpn(e.target.value);
                  }}
                />
              </FormField>
              <FormField id="ob-custom-dn" label="Unique display name">
                <input
                  id="ob-custom-dn"
                  className="rc-input"
                  value={customDisplayName}
                  onChange={(e) => {
                    setCustomDisplayName(e.target.value);
                  }}
                />
              </FormField>
            </div>
          </AlertBanner>
        ) : null}

        <div className="rc-row">
          <Button type="submit" variant="primary" disabled={busy || !selectedTemplateId}>
            {busy ? 'Building preview…' : 'Review onboarding changes'}
          </Button>
        </div>
      </form>

      {planResp ? (
        <ChangePreview
          plan={planResp.plan}
          isSubmitting={busy}
          primaryActionLabel={`Run onboarding (${String(planResp.plan.actionableChangeCount)} changes)`}
          onConfirm={() => void handleSubmit()}
        />
      ) : null}
    </div>
  );
}

export function OffboardingPage() {
  const { id = '' } = useParams();
  const navigate = useNavigate();
  const usersQuery = useQuery({
    queryKey: ['users', 'offboard-picker'],
    queryFn: () => apiRequest<DirectoryUserDto[]>('/api/users?limit=100'),
  });

  const [selectedUserId, setSelectedUserId] = useState(id);
  const [leaveDate, setLeaveDate] = useState('');
  const [convertToShared, setConvertToShared] = useState(true);
  const [forwardToUpn, setForwardToUpn] = useState('');
  const [deliverAndForward, setDeliverAndForward] = useState(true);
  const [autoReply, setAutoReply] = useState('');
  const [grantFullAccess, setGrantFullAccess] = useState(true);
  const [hideFromGal, setHideFromGal] = useState(true);
  const [grantOneDrive, setGrantOneDrive] = useState(true);
  const [oneDriveDays, setOneDriveDays] = useState(30);
  const [removeAllLicenses, setRemoveAllLicenses] = useState(true);
  const [previewPlan, setPreviewPlan] = useState<PlanSummaryDto | null>(null);
  const [confirmOpen, setConfirmOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const selectedUser = (usersQuery.data ?? []).find((u) => u.id === selectedUserId);

  const buildPayload = (submit: boolean, confirmationUpn?: string) => ({
    userId: selectedUserId,
    leaveDate: leaveDate ? new Date(leaveDate).toISOString() : null,
    mailbox: {
      convertToShared,
      forwardToUpn: forwardToUpn.trim() || null,
      deliverToMailboxAndForward: deliverAndForward,
      autoReplyInternal: autoReply.trim() || null,
      autoReplyExternal: autoReply.trim() || null,
      grantManagerFullAccess: grantFullAccess,
      grantManagerSendAs: false,
      hideFromAddressLists: hideFromGal,
    },
    oneDrive: {
      grantManagerAccess: grantOneDrive,
      accessRetentionDays: oneDriveDays,
    },
    removeAllLicenses,
    keepSkuIds: null,
    clearManager: true,
    prefixDescriptionWithLeftDate: true,
    offboardingOuOverride: null,
    confirmationUpn: confirmationUpn ?? null,
    confirmPrivilegedRoleAsAdmin: true,
    submit,
  });

  const handlePreview = async (e: React.SyntheticEvent) => {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<{ plan: PlanSummaryDto }>('/api/offboarding', {
        method: 'POST',
        body: buildPayload(false),
      });
      setPreviewPlan(res.plan);
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not build offboarding preview.');
    } finally {
      setBusy(false);
    }
  };

  const handleConfirmedSubmit = async () => {
    if (!selectedUser) return;
    setError(null);
    setBusy(true);
    try {
      const res = await apiRequest<{ run: WorkflowRunDto | null }>('/api/offboarding', {
        method: 'POST',
        body: buildPayload(true, selectedUser.userPrincipalName),
      });
      setConfirmOpen(false);
      if (res.run) {
        void navigate(`/runs/${res.run.id}`);
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Offboarding failed.');
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="rc-stack">
      <div className="rc-page-header">
        <div>
          <h1 className="rc-page-title">Offboard user</h1>
          <p className="rc-muted" style={{ margin: 0 }}>
            Blocks sign-in, revokes sessions, converts the mailbox before removing licenses, and
            records undo data for reversal.
          </p>
        </div>
      </div>

      {error ? <AlertBanner tone="danger">{error}</AlertBanner> : null}

      <form className="rc-card" onSubmit={(e) => void handlePreview(e)}>
        <div className="rc-grid-2">
          <FormField id="off-user" label="User to offboard">
            <select
              id="off-user"
              className="rc-select"
              value={selectedUserId}
              onChange={(e) => {
                setSelectedUserId(e.target.value);
              }}
              required
            >
              <option value="">Select a user…</option>
              {(usersQuery.data ?? []).map((u) => (
                <option key={u.id} value={u.id}>
                  {u.displayName} ({u.userPrincipalName})
                </option>
              ))}
            </select>
          </FormField>
          <FormField
            id="off-date"
            label="Leave date and time"
            optional
            helper="Leave empty to run immediately."
          >
            <input
              id="off-date"
              type="datetime-local"
              className="rc-input"
              value={leaveDate}
              onChange={(e) => {
                setLeaveDate(e.target.value);
              }}
            />
          </FormField>
        </div>

        <div className="rc-grid-2">
          <div className="rc-stack">
            <h2 className="rc-section-title">Mailbox handling</h2>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={convertToShared}
                onChange={(e) => {
                  setConvertToShared(e.target.checked);
                }}
              />
              Convert to shared mailbox (verified before removing licenses)
            </label>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={grantFullAccess}
                onChange={(e) => {
                  setGrantFullAccess(e.target.checked);
                }}
              />
              Grant manager Full Access
            </label>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={hideFromGal}
                onChange={(e) => {
                  setHideFromGal(e.target.checked);
                }}
              />
              Hide from address lists
            </label>
            <FormField id="off-fwd" label="Forward mail to UPN" optional>
              <input
                id="off-fwd"
                className="rc-input rc-input-mono"
                value={forwardToUpn}
                onChange={(e) => {
                  setForwardToUpn(e.target.value);
                }}
              />
            </FormField>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={deliverAndForward}
                onChange={(e) => {
                  setDeliverAndForward(e.target.checked);
                }}
              />
              Keep a copy in the shared mailbox when forwarding
            </label>
            <FormField id="off-ooo" label="Automatic reply message" optional>
              <textarea
                id="off-ooo"
                className="rc-textarea"
                value={autoReply}
                onChange={(e) => {
                  setAutoReply(e.target.value);
                }}
              />
            </FormField>
          </div>

          <div className="rc-stack">
            <h2 className="rc-section-title">OneDrive and licenses</h2>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={grantOneDrive}
                onChange={(e) => {
                  setGrantOneDrive(e.target.checked);
                }}
              />
              Grant manager temporary OneDrive access
            </label>
            <FormField id="off-od-days" label="OneDrive access duration (days)">
              <input
                id="off-od-days"
                type="number"
                min={1}
                max={365}
                className="rc-input"
                value={oneDriveDays}
                onChange={(e) => {
                  setOneDriveDays(Number(e.target.value));
                }}
              />
            </FormField>
            <label className="rc-checkbox-label">
              <input
                type="checkbox"
                checked={removeAllLicenses}
                onChange={(e) => {
                  setRemoveAllLicenses(e.target.checked);
                }}
              />
              Remove all directly assigned licenses after mailbox conversion
            </label>
          </div>
        </div>

        <div className="rc-row">
          <Button type="submit" variant="primary" disabled={busy || !selectedUserId}>
            {busy ? 'Building preview…' : 'Review offboarding changes'}
          </Button>
        </div>
      </form>

      {previewPlan && selectedUser ? (
        <>
          <ChangePreview
            plan={previewPlan}
            primaryActionLabel={`Run offboarding (${String(previewPlan.actionableChangeCount)} changes)`}
            onConfirm={() => {
              setConfirmOpen(true);
            }}
          />
          <DestructiveConfirmDialog
            open={confirmOpen}
            onOpenChange={setConfirmOpen}
            title={`Offboard ${selectedUser.displayName}`}
            impactSentence={`This blocks sign-in for ${selectedUser.displayName} immediately and signs them out of every session. Type their UPN to confirm.`}
            expectedText={selectedUser.userPrincipalName}
            inputLabel="User principal name"
            confirmButtonLabel="Run offboarding"
            onConfirm={() => void handleConfirmedSubmit()}
            isBusy={busy}
          />
        </>
      ) : null}
    </div>
  );
}
