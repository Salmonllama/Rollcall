// SPDX-License-Identifier: GPL-3.0-or-later

export type PortalRole = 'Technician' | 'Approver' | 'Admin';

export type WorkflowRunStatus =
  | 'Draft'
  | 'Planned'
  | 'AwaitingApproval'
  | 'Scheduled'
  | 'Queued'
  | 'Running'
  | 'WaitingForAgent'
  | 'NeedsReview'
  | 'Succeeded'
  | 'PartiallyFailed'
  | 'Rejected'
  | 'Cancelled';

export type WorkflowStepStatus =
  'Pending' | 'Running' | 'Waiting' | 'Succeeded' | 'Failed' | 'Skipped';

export type StepOperation = 'Add' | 'Remove' | 'Change' | 'NoOp' | 'Info';
export type TargetSystem = 'EntraId' | 'ExchangeOnline' | 'ActiveDirectory' | 'OneDrive' | 'Portal';

export interface CurrentUserDto {
  isAuthenticated: boolean;
  setupCompleted: boolean;
  id: string | null;
  entraObjectId: string | null;
  upn: string | null;
  displayName: string | null;
  roles: PortalRole[];
  disabled: boolean;
  tenantDomain: string | null;
  organizationName: string | null;
  features: Record<string, boolean>;
  showCertExpiryWarning: boolean;
  certDaysUntilExpiry: number | null;
  csrfToken: string | null;
}

export interface StepPlanDto {
  sequence: number;
  stepKind: string;
  targetSystem: TargetSystem;
  area: string;
  operation: StepOperation;
  description: string;
  objectName: string;
  oldValue: string | null;
  newValue: string | null;
  isNoOp: boolean;
  noOpReason: string | null;
  requiresApproval: boolean;
  warnings: string[];
}

export interface PlanSummaryDto {
  actionableChangeCount: number;
  noOpCount: number;
  systemCount: number;
  approvalRequiredCount: number;
  systems: TargetSystem[];
  warnings: string[];
  blockingErrors: string[];
  planHash: string;
  steps: StepPlanDto[];
}

export interface WorkflowStepDto {
  id: string;
  sequence: number;
  kind: string;
  targetSystem: TargetSystem;
  area: string;
  description: string;
  objectName: string;
  status: WorkflowStepStatus;
  attempts: number;
  plainLanguageError: string | null;
  rawError: string | null;
  graphRequestId: string | null;
  graphClientRequestId: string | null;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  hasUndoData: boolean;
}

export interface ApprovalDto {
  id: string;
  runId: string;
  approverUpn: string;
  approverDisplayName: string;
  decision: 'Approved' | 'Rejected';
  comment: string | null;
  decidedAt: string;
}

export interface WorkflowRunDto {
  id: string;
  type: string;
  status: WorkflowRunStatus;
  requestedByUpn: string;
  requestedByDisplayName: string;
  targetUserRef: string;
  targetUserDisplayName: string | null;
  createdAt: string;
  scheduledFor: string | null;
  startedAt: string | null;
  completedAt: string | null;
  planHash: string;
  plan: PlanSummaryDto;
  steps: WorkflowStepDto[];
  approvals: ApprovalDto[];
  roleTemplateId: string | null;
  roleTemplateVersion: number | null;
  canApprove: boolean;
  canRetry: boolean;
  canRestore: boolean;
  hasUnviewedCredential: boolean;
  statusReason: string | null;
}

export interface WorkflowRunListItemDto {
  id: string;
  type: string;
  status: WorkflowRunStatus;
  requestedByUpn: string;
  requestedByDisplayName: string;
  targetUserRef: string;
  targetUserDisplayName: string | null;
  createdAt: string;
  scheduledFor: string | null;
  completedAt: string | null;
  totalSteps: number;
  completedSteps: number;
  failedSteps: number;
  actionableChanges: number;
}

export interface DirectoryUserDto {
  id: string;
  userPrincipalName: string;
  displayName: string;
  mail: string | null;
  jobTitle: string | null;
  department: string | null;
  officeLocation: string | null;
  employeeId: string | null;
  usageLocation: string | null;
  companyName: string | null;
  businessPhone: string | null;
  mobilePhone: string | null;
  accountEnabled: boolean;
  onPremisesSyncEnabled: boolean;
  onPremisesSamAccountName: string | null;
  onPremisesDistinguishedName: string | null;
  userType: string;
  licenseCount: number;
  createdDateTime: string | null;
}

export interface DirectoryGroupDto {
  id: string;
  displayName: string;
  mail: string | null;
  description: string | null;
  groupType: string;
  onPremisesSyncEnabled: boolean;
  onPremisesDistinguishedName: string | null;
  memberCount: number;
}

export interface UserLicenseAssignmentDto {
  skuId: string;
  skuPartNumber: string;
  friendlyName: string;
  assignedViaGroup: boolean;
  sourceGroupId: string | null;
  sourceGroupName: string | null;
  disabledPlans: string[];
}

export interface MfaMethodDto {
  id: string;
  methodType: string;
  displayName: string;
  detail: string | null;
  createdDateTime: string | null;
  removable: boolean;
}

export interface CaPolicyApplicabilityDto {
  policyId: string;
  displayName: string;
  state: string;
  applicability: string;
  reason: string;
  temporaryExclusionExpiresAt: string | null;
}

export interface DirectoryUserDetailDto {
  user: DirectoryUserDto;
  manager: DirectoryUserDto | null;
  groups: DirectoryGroupDto[];
  licenses: UserLicenseAssignmentDto[];
  mfaMethods: MfaMethodDto[];
  conditionalAccessPolicies: CaPolicyApplicabilityDto[];
  directoryRoles: string[];
  customAttributes: Record<string, string>;
  recentRuns: WorkflowRunListItemDto[];
  recentAuditEvents: AuditEventDto[];
}

export interface LicenseSkuDto {
  skuId: string;
  skuPartNumber: string;
  friendlyName: string;
  totalSeats: number;
  consumedSeats: number;
  availableSeats: number;
}

export interface LicensesOverviewDto {
  skus: LicenseSkuDto[];
  licensedDisabledUsers: {
    userId: string;
    userPrincipalName: string;
    displayName: string;
    licenses: UserLicenseAssignmentDto[];
  }[];
}

export interface ConditionalAccessPolicyDto {
  id: string;
  displayName: string;
  state: string;
  eTag: string;
  includeUsers: string[];
  excludeUsers: string[];
  includeGroups: string[];
  excludeGroups: string[];
  includeRoles: string[];
  excludeRoles: string[];
  grantControls: string[];
  requiresMfa: boolean;
  blocksLegacyAuth: boolean;
  conditionsSummary: string;
  activeTemporaryExclusions: {
    id: string;
    targetRef: string;
    description: string;
    dueAt: string;
  }[];
}

export interface DashboardSummaryDto {
  upcomingOnboardingsCount: number;
  upcomingOffboardingsCount: number;
  pendingApprovalsCount: number;
  recentFailuresCount: number;
  availableLicenseSeats: number;
  licensedDisabledAccountsCount: number;
  showCertExpiryWarning: boolean;
  certDaysUntilExpiry: number | null;
  connectedAgentsCount: number;
  offlineAgentsCount: number;
  upcomingOnboardings: WorkflowRunListItemDto[];
  upcomingOffboardings: WorkflowRunListItemDto[];
  pendingApprovals: WorkflowRunListItemDto[];
  recentFailures: WorkflowRunListItemDto[];
}

export interface AuditEventDto {
  id: number;
  occurredAt: string;
  actorUpn: string;
  actorDisplayName: string;
  action: string;
  target: string;
  outcome: string;
  runId: string | null;
  summary: string | null;
  detailsJson: string;
  prevHash: string;
  hash: string;
}

export interface CursorPage<T> {
  items: T[];
  nextCursor: string | null;
  totalCount: number;
}

export interface RoleTemplateDefinition {
  accountType: string;
  upnPattern: string;
  displayNamePattern: string;
  department: string | null;
  jobTitle: string | null;
  companyName: string | null;
  officeLocation: string | null;
  usageLocation: string | null;
  targetOu: string | null;
  cloudGroupIds: string[];
  distributionListIds: string[];
  adGroupDns: string[];
  licenses: { skuId: string; disabledPlans: string[] }[];
  credentialMode: string;
  tapLifetimeMinutes: number;
  tapUsableOnce: boolean;
  createBlockedUntilStartDate: boolean;
  mailboxTimeZone: string | null;
  mailboxLanguage: string | null;
  managerNotificationText: string | null;
  overridableFields: string[];
  assignsAdminRoles: boolean;
}

export interface RoleTemplateDto {
  id: string;
  name: string;
  description: string | null;
  version: number;
  isArchived: boolean;
  eTag: string;
  definition: RoleTemplateDefinition;
  createdAt: string;
  updatedAt: string;
  updatedByUpn: string;
}

export interface PortalUserDto {
  id: string;
  entraObjectId: string;
  upn: string;
  displayName: string;
  roles: PortalRole[];
  disabled: boolean;
  lastSignInAt: string | null;
  createdAt: string;
}

export interface ApprovalRuleDto {
  actionType: string;
  label: string;
  description: string;
  required: boolean;
  approverRole: PortalRole;
  lockedRequired: boolean;
}

export interface HealthOverviewDto {
  overallStatus: string;
  checkedAt: string;
  version: string;
  queueDepth: number;
  failedRunsLast24Hours: number;
  lastBackupAt: string | null;
  diskFreeBytes: number | null;
  diskTotalBytes: number | null;
  components: {
    name: string;
    status: string;
    summary: string;
    detail: string | null;
  }[];
}

export interface PortalSettingsDto {
  tenantTimeZone: string;
  defaultScheduleTimeLocal: string;
  defaultUsageLocation: string;
  features: Record<string, boolean>;
  breakGlassAccountIds: string[];
  breakGlassGroupIds: string[];
  runPayloadRetentionDays: number | null;
  eTag: string;
}

export interface ConnectionsOverviewDto {
  tenantId: string | null;
  appClientId: string | null;
  primaryDomain: string | null;
  organizationName: string | null;
  connectedAt: string | null;
  activeCertificate: AppCertificateSummaryDto | null;
  pendingCertificate: AppCertificateSummaryDto | null;
  retiringCertificate: AppCertificateSummaryDto | null;
  showExpiryWarning: boolean;
  permissions: PermissionCheckItemDto[];
  directoryRoles: PermissionCheckItemDto[];
  lastCheckError: string | null;
}

export interface AppCertificateSummaryDto {
  id: string;
  thumbprint: string;
  subject: string;
  algorithm: string;
  notBefore: string;
  notAfter: string;
  status: string;
  daysUntilExpiry: number;
}

export interface PermissionCheckItemDto {
  name: string;
  api: string;
  kind: string;
  granted: boolean;
  affectedFeatures: string[];
}

export interface AdAgentDto {
  id: string;
  name: string;
  status: string;
  version: string | null;
  hostname: string | null;
  updateAvailable: boolean;
  lastSeenAt: string | null;
  createdAt: string;
}
