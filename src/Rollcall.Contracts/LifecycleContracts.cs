// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record RoleTemplateDefinition(
    string AccountType,
    string UpnPattern,
    string DisplayNamePattern,
    string? Department,
    string? JobTitle,
    string? CompanyName,
    string? OfficeLocation,
    string? UsageLocation,
    string? TargetOu,
    IReadOnlyList<string> CloudGroupIds,
    IReadOnlyList<string> DistributionListIds,
    IReadOnlyList<string> AdGroupDns,
    IReadOnlyList<TemplateLicenseSpec> Licenses,
    string CredentialMode,
    int TapLifetimeMinutes,
    bool TapUsableOnce,
    bool CreateBlockedUntilStartDate,
    string? MailboxTimeZone,
    string? MailboxLanguage,
    string? ManagerNotificationText,
    IReadOnlyList<string> OverridableFields,
    bool AssignsAdminRoles);

public sealed record TemplateLicenseSpec(
    string SkuId,
    IReadOnlyList<string> DisabledPlans);

public sealed record RoleTemplateDto(
    Guid Id,
    string Name,
    string? Description,
    int Version,
    bool IsArchived,
    string ETag,
    RoleTemplateDefinition Definition,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string UpdatedByUpn);

public sealed record SaveRoleTemplateRequest(
    string Name,
    string? Description,
    RoleTemplateDefinition Definition);

public sealed record TemplateValidationResultDto(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public sealed record OnboardingRequest(
    Guid RoleTemplateId,
    string FirstName,
    string LastName,
    string? CustomUpn,
    string? CustomMailNickname,
    string? CustomDisplayName,
    string? CustomSamAccountName,
    string? Department,
    string? JobTitle,
    string? ManagerId,
    string? OfficeLocation,
    string? UsageLocation,
    DateTimeOffset? StartDate,
    string? TargetOuOverride,
    bool Submit);

public sealed record OnboardingCollisionDto(
    string Field,
    string ProposedValue,
    string ConflictingObjectDisplayName,
    string ConflictingObjectIdentifier,
    TargetSystem System);

public sealed record OnboardingPlanResponse(
    PlanSummaryDto Plan,
    IReadOnlyList<OnboardingCollisionDto> Collisions,
    WorkflowRunDto? Run);

public sealed record WelcomePackSettingsDto(
    string IntroText,
    string FirstSignInSteps,
    string MfaSetupSteps,
    bool HideRollcallBranding,
    string? OrganizationName,
    bool HasHeaderImage,
    string? HeaderImageContentType,
    bool HasLetterhead,
    string? LetterheadContentType,
    int ContentMarginTopPt,
    int ContentMarginRightPt,
    int ContentMarginBottomPt,
    int ContentMarginLeftPt);

public sealed record WelcomePackViewDto(
    Guid RunId,
    string DisplayName,
    string UserPrincipalName,
    DateTimeOffset? StartDate,
    bool CredentialAvailable,
    string? CredentialKind,
    string? CredentialValue,
    DateTimeOffset? CredentialExpiresAt,
    string? CredentialAlreadyShownMessage,
    WelcomePackSettingsDto Branding);

public sealed record MailboxOffboardingOptions(
    bool ConvertToShared,
    string? ForwardToUpn,
    bool DeliverToMailboxAndForward,
    string? AutoReplyInternal,
    string? AutoReplyExternal,
    bool GrantManagerFullAccess,
    bool GrantManagerSendAs,
    bool HideFromAddressLists);

public sealed record OneDriveOffboardingOptions(
    bool GrantManagerAccess,
    int AccessRetentionDays);

public sealed record OffboardingRequest(
    string UserId,
    DateTimeOffset? LeaveDate,
    MailboxOffboardingOptions Mailbox,
    OneDriveOffboardingOptions OneDrive,
    bool RemoveAllLicenses,
    IReadOnlyList<string>? KeepSkuIds,
    bool ClearManager,
    bool PrefixDescriptionWithLeftDate,
    string? OffboardingOuOverride,
    string? ConfirmationUpn,
    bool ConfirmPrivilegedRoleAsAdmin,
    bool Submit);

public sealed record RestoreUserRequest(
    Guid OffboardingRunId,
    bool ReEnableSignIn,
    bool RestoreGroups,
    bool RestoreLicenses,
    bool Submit);

public sealed record ConditionalAccessPolicyDto(
    string Id,
    string DisplayName,
    string State,
    string ETag,
    IReadOnlyList<string> IncludeUsers,
    IReadOnlyList<string> ExcludeUsers,
    IReadOnlyList<string> IncludeGroups,
    IReadOnlyList<string> ExcludeGroups,
    IReadOnlyList<string> IncludeRoles,
    IReadOnlyList<string> ExcludeRoles,
    IReadOnlyList<string> GrantControls,
    bool RequiresMfa,
    bool BlocksLegacyAuth,
    string ConditionsSummary,
    IReadOnlyList<ScheduledReversalDto> ActiveTemporaryExclusions);

public sealed record EditCaPolicyMembershipRequest(
    IReadOnlyList<string> IncludeUsers,
    IReadOnlyList<string> ExcludeUsers,
    IReadOnlyList<string> IncludeGroups,
    IReadOnlyList<string> ExcludeGroups,
    string? ExpectedETag,
    string? ConfirmationTarget,
    bool Submit);

public sealed record AddTemporaryCaExclusionRequest(
    string PolicyId,
    string UserId,
    DateTimeOffset ExpiresAt,
    string? Reason,
    bool Submit);

public sealed record ScheduledReversalDto(
    Guid Id,
    Guid RunId,
    string Kind,
    string TargetRef,
    string Description,
    DateTimeOffset DueAt,
    bool Executed,
    DateTimeOffset? ExecutedAt,
    string? Outcome);
