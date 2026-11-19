// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography.X509Certificates;
using Rollcall.Contracts;

namespace Rollcall.Infrastructure.Integrations;

public sealed record GraphOrganizationInfo(
    string TenantId,
    string DisplayName,
    string PrimaryDomain,
    IReadOnlyList<string> GrantedAppPermissions,
    IReadOnlyList<string> AssignedDirectoryRoles);

public sealed record GraphCreateUserRequest(
    string DisplayName,
    string UserPrincipalName,
    string MailNickname,
    string GivenName,
    string Surname,
    string Password,
    bool ForceChangePasswordNextSignIn,
    bool AccountEnabled,
    string? Department,
    string? JobTitle,
    string? OfficeLocation,
    string? CompanyName,
    string? UsageLocation);

public sealed record GraphTapResult(
    string Id,
    string TemporaryAccessPass,
    DateTimeOffset StartDateTime,
    int LifetimeInMinutes,
    bool IsUsableOnce);

public sealed record GraphBatchOperation(
    string Id,
    string Method,
    string Url,
    object? Body = null);

public sealed record GraphBatchItemResult(
    string Id,
    int Status,
    string? ErrorMessage);

public sealed record GraphOneDriveGrant(
    string PermissionId,
    string GranteeUpn,
    string Role);

public interface IGraphClient
{
    Task<GraphOrganizationInfo> ValidateConnectionAsync(
        string tenantId,
        string clientId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectoryUserDto>> SearchUsersAsync(
        string? query,
        string? department,
        bool? accountEnabled,
        bool? onPremisesSyncEnabled,
        bool? licensed,
        int limit,
        CancellationToken cancellationToken = default);

    Task<DirectoryUserDto?> GetUserAsync(string userIdOrUpn, CancellationToken cancellationToken = default);
    Task<DirectoryUserDto?> GetUserBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken = default);
    Task<DirectoryUserDto?> GetManagerAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DirectoryGroupDto>> GetUserGroupsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLicenseAssignmentDto>> GetUserLicensesAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MfaMethodDto>> GetUserMfaMethodsAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetUserDirectoryRolesAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetUserOwnedGroupNamesAsync(string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, string>> GetUserCustomAttributesAsync(string userId, CancellationToken cancellationToken = default);

    Task<DirectoryUserDto> CreateUserAsync(GraphCreateUserRequest request, CancellationToken cancellationToken = default);
    Task UpdateUserAttributesAsync(string userId, IReadOnlyDictionary<string, object?> patchFields, CancellationToken cancellationToken = default);
    Task SetManagerAsync(string userId, string? managerId, CancellationToken cancellationToken = default);
    Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default);
    Task ResetPasswordAsync(string userId, string newPassword, bool forceChangeNextSignIn, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectoryGroupDto>> ListGroupsAsync(string? query, CancellationToken cancellationToken = default);
    Task<DirectoryGroupDetailDto?> GetGroupDetailAsync(string groupId, CancellationToken cancellationToken = default);
    Task AddGroupMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default);
    Task RemoveGroupMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GraphBatchItemResult>> ExecuteBatchAsync(IReadOnlyList<GraphBatchOperation> operations, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LicenseSkuDto>> ListSubscribedSkusAsync(CancellationToken cancellationToken = default);
    Task AssignLicensesAsync(
        string userId,
        IReadOnlyList<TemplateLicenseSpec> addLicenses,
        IReadOnlyList<string> removeSkuIds,
        CancellationToken cancellationToken = default);

    Task RemoveMfaMethodAsync(string userId, string methodType, string methodId, CancellationToken cancellationToken = default);
    Task<GraphTapResult> IssueTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConditionalAccessPolicyDto>> ListConditionalAccessPoliciesAsync(CancellationToken cancellationToken = default);
    Task<ConditionalAccessPolicyDto?> GetConditionalAccessPolicyAsync(string policyId, CancellationToken cancellationToken = default);
    Task PatchConditionalAccessUsersAsync(
        string policyId,
        IReadOnlyList<string> includeUsers,
        IReadOnlyList<string> excludeUsers,
        IReadOnlyList<string> includeGroups,
        IReadOnlyList<string> excludeGroups,
        CancellationToken cancellationToken = default);

    Task<GraphOneDriveGrant> GrantOneDriveAccessAsync(string userId, string managerUpn, CancellationToken cancellationToken = default);
    Task RevokeOneDriveAccessAsync(string userId, string permissionId, CancellationToken cancellationToken = default);
    Task<int?> GetOneDriveRetentionDaysAsync(CancellationToken cancellationToken = default);
}
