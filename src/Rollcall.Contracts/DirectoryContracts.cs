// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record DirectoryUserDto(
    string Id,
    string UserPrincipalName,
    string DisplayName,
    string? Mail,
    string? JobTitle,
    string? Department,
    string? OfficeLocation,
    string? EmployeeId,
    string? UsageLocation,
    string? CompanyName,
    string? BusinessPhone,
    string? MobilePhone,
    bool AccountEnabled,
    bool OnPremisesSyncEnabled,
    string? OnPremisesSamAccountName,
    string? OnPremisesDistinguishedName,
    string UserType,
    int LicenseCount,
    DateTimeOffset? CreatedDateTime);

public sealed record DirectoryGroupDto(
    string Id,
    string DisplayName,
    string? Mail,
    string? Description,
    string GroupType,
    bool OnPremisesSyncEnabled,
    string? OnPremisesDistinguishedName,
    int MemberCount);

public sealed record UserLicenseAssignmentDto(
    string SkuId,
    string SkuPartNumber,
    string FriendlyName,
    bool AssignedViaGroup,
    string? SourceGroupId,
    string? SourceGroupName,
    IReadOnlyList<string> DisabledPlans);

public sealed record MfaMethodDto(
    string Id,
    string MethodType,
    string DisplayName,
    string? Detail,
    DateTimeOffset? CreatedDateTime,
    bool Removable);

public sealed record CaPolicyApplicabilityDto(
    string PolicyId,
    string DisplayName,
    string State,
    string Applicability,
    string Reason,
    DateTimeOffset? TemporaryExclusionExpiresAt);

public sealed record DirectoryUserDetailDto(
    DirectoryUserDto User,
    DirectoryUserDto? Manager,
    IReadOnlyList<DirectoryGroupDto> Groups,
    IReadOnlyList<UserLicenseAssignmentDto> Licenses,
    IReadOnlyList<MfaMethodDto> MfaMethods,
    IReadOnlyList<CaPolicyApplicabilityDto> ConditionalAccessPolicies,
    IReadOnlyList<string> DirectoryRoles,
    IReadOnlyDictionary<string, string> CustomAttributes,
    IReadOnlyList<WorkflowRunListItemDto> RecentRuns,
    IReadOnlyList<AuditEventDto> RecentAuditEvents);

public sealed record DirectoryGroupDetailDto(
    DirectoryGroupDto Group,
    IReadOnlyList<DirectoryUserDto> Members,
    IReadOnlyList<DirectoryUserDto> Owners);

public sealed record LicenseSkuDto(
    string SkuId,
    string SkuPartNumber,
    string FriendlyName,
    int TotalSeats,
    int ConsumedSeats,
    int AvailableSeats,
    IReadOnlyList<ServicePlanDto> ServicePlans);

public sealed record ServicePlanDto(
    string ServicePlanId,
    string ServicePlanName,
    string AppliesTo);

public sealed record LicensedDisabledUserDto(
    string UserId,
    string UserPrincipalName,
    string DisplayName,
    IReadOnlyList<UserLicenseAssignmentDto> Licenses);

public sealed record LicensesOverviewDto(
    IReadOnlyList<LicenseSkuDto> Skus,
    IReadOnlyList<LicensedDisabledUserDto> LicensedDisabledUsers);

public sealed record DashboardSummaryDto(
    int UpcomingOnboardingsCount,
    int UpcomingOffboardingsCount,
    int PendingApprovalsCount,
    int RecentFailuresCount,
    int AvailableLicenseSeats,
    int LicensedDisabledAccountsCount,
    bool ShowCertExpiryWarning,
    int? CertDaysUntilExpiry,
    int ConnectedAgentsCount,
    int OfflineAgentsCount,
    IReadOnlyList<WorkflowRunListItemDto> UpcomingOnboardings,
    IReadOnlyList<WorkflowRunListItemDto> UpcomingOffboardings,
    IReadOnlyList<WorkflowRunListItemDto> PendingApprovals,
    IReadOnlyList<WorkflowRunListItemDto> RecentFailures);

public sealed record UpdateUserAttributesRequest(
    string? JobTitle,
    string? Department,
    string? ManagerId,
    string? OfficeLocation,
    string? BusinessPhone,
    string? MobilePhone,
    string? EmployeeId,
    string? UsageLocation,
    string? CompanyName,
    IReadOnlyDictionary<string, string>? CustomAttributes,
    bool Submit);

public sealed record BulkUpdateAttributesRequest(
    IReadOnlyList<string> UserIds,
    string? JobTitle,
    string? Department,
    string? OfficeLocation,
    string? CompanyName,
    string? UsageLocation,
    IReadOnlyDictionary<string, string>? CustomAttributes,
    bool Submit);

public sealed record GroupMembershipChangeRequest(
    IReadOnlyList<string> UserIds,
    IReadOnlyList<string> GroupIds,
    string Action,
    bool Submit);

public sealed record CopyGroupsRequest(
    string SourceUserId,
    string TargetUserId,
    bool Submit);

public sealed record LicenseChangeRequest(
    IReadOnlyList<string> UserIds,
    IReadOnlyList<string> AddSkuIds,
    IReadOnlyList<string> RemoveSkuIds,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? DisabledPlansBySku,
    bool Submit);

public sealed record RemoveMfaMethodRequest(
    string MethodId,
    string MethodType,
    bool Submit);

public sealed record IssueTapRequest(
    int LifetimeInMinutes,
    bool IsUsableOnce,
    bool Submit);

public sealed record IssuedCredentialDto(
    string CredentialKind,
    string UserPrincipalName,
    string Value,
    DateTimeOffset? ExpiresAt,
    bool OneTimeOnly);

public sealed record ResetPasswordRequest(
    bool ForceChangeNextSignIn,
    bool Submit);
