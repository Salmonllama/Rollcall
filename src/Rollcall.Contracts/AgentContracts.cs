// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record AdAgentDto(
    Guid Id,
    string Name,
    AgentStatus Status,
    string? Version,
    string? Hostname,
    bool UpdateAvailable,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt);

public sealed record CreateAgentEnrollmentRequest(string Name);

public sealed record AgentEnrollmentCommandDto(
    Guid AgentId,
    string Name,
    string EnrollmentToken,
    DateTimeOffset ExpiresAt,
    string PowerShellInstallCommand);

public sealed record AgentEnrollRequest(
    string EnrollmentToken,
    string PublicKeyPem,
    string Hostname,
    string Version,
    int ProtocolVersion);

public sealed record AgentEnrollResponse(
    Guid AgentId,
    string Name,
    IReadOnlyList<string> AllowedOus,
    string? EntraConnectServer,
    int MinimumProtocolVersion);

public sealed record AgentHeartbeatRequest(
    string Hostname,
    string Version,
    int ProtocolVersion);

public sealed record AgentHeartbeatResponse(
    IReadOnlyList<string> AllowedOus,
    string? EntraConnectServer,
    bool Revoked);

public sealed record AgentJobLeaseRequest(
    int MaxJobs,
    int WaitSeconds,
    string Version,
    int ProtocolVersion);

public sealed record AgentJobLeaseItemDto(
    Guid JobId,
    Guid? RunId,
    Guid? StepId,
    string Kind,
    string PayloadJson,
    DateTimeOffset LeaseUntil,
    IReadOnlyList<string> AllowedOus);

public sealed record AgentJobLeaseResponse(
    IReadOnlyList<AgentJobLeaseItemDto> Jobs);

public sealed record AgentJobProgressRequest(
    string Message,
    int? PercentComplete);

public sealed record AgentJobResultRequest(
    bool Succeeded,
    string? ResultJson,
    string? PlainLanguageError,
    string? RawError,
    bool IsTransient);

public sealed record AdOuDto(
    string DistinguishedName,
    string Name,
    string? Description);

public sealed record AdSettingsDto(
    IReadOnlyList<string> AllowedOus,
    string? DefaultNewUserOu,
    string? OffboardingOu,
    string? EntraConnectServer,
    int SyncWaitTimeoutMinutes,
    int AgentOfflineTimeoutMinutes);

public sealed record AdUserCreatePayload(
    string SamAccountName,
    string UserPrincipalName,
    string DisplayName,
    string FirstName,
    string LastName,
    string TargetOu,
    string? Department,
    string? Title,
    string? Office,
    string? Company,
    string? EmployeeId,
    string? ManagerDn,
    bool Enabled,
    string? InitialPassword);

public sealed record AdUserUpdatePayload(
    string SamAccountNameOrDn,
    string? TargetOu,
    string? DisplayName,
    string? Department,
    string? Title,
    string? Office,
    string? Phone,
    string? Mobile,
    string? Company,
    string? EmployeeId,
    string? ManagerDn,
    string? Description);

public sealed record AdUserSetEnabledPayload(string SamAccountNameOrDn, bool Enabled);

public sealed record AdUserResetPasswordPayload(string SamAccountNameOrDn, string NewPassword, bool MustChangeAtNextLogon);

public sealed record AdUserMovePayload(string SamAccountNameOrDn, string DestinationOu);

public sealed record AdUserRemoveFromAllGroupsPayload(string SamAccountNameOrDn);

public sealed record AdGroupCreatePayload(
    string Name,
    string SamAccountName,
    string TargetOu,
    string Scope,
    string Category,
    string? Description);

public sealed record AdGroupUpdatePayload(
    string GroupDnOrSam,
    string? NewName,
    string? Description,
    string? Scope,
    string? Category);

public sealed record AdGroupMovePayload(string GroupDnOrSam, string DestinationOu);

public sealed record AdGroupMembershipPayload(string GroupDnOrSam, string MemberDnOrSam);

public sealed record AdSyncDeltaPayload(string? EntraConnectServer);

public sealed record AdGroupManagementRequest(
    string Operation,
    string? GroupDnOrSam,
    string? Name,
    string? SamAccountName,
    string? TargetOu,
    string? Scope,
    string? Category,
    string? Description,
    string? MemberDnOrSam,
    bool Submit);
