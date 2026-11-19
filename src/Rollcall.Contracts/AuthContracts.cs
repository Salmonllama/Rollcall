// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record CurrentUserDto(
    bool IsAuthenticated,
    bool SetupCompleted,
    Guid? Id,
    string? EntraObjectId,
    string? Upn,
    string? DisplayName,
    IReadOnlyList<PortalRole> Roles,
    bool Disabled,
    string? TenantDomain,
    string? OrganizationName,
    IReadOnlyDictionary<string, bool> Features,
    bool ShowCertExpiryWarning,
    int? CertDaysUntilExpiry,
    string? CsrfToken);

public sealed record PortalUserDto(
    Guid Id,
    string EntraObjectId,
    string Upn,
    string DisplayName,
    IReadOnlyList<PortalRole> Roles,
    bool Disabled,
    DateTimeOffset? LastSignInAt,
    DateTimeOffset CreatedAt);

public sealed record InvitePortalUserRequest(
    string Upn,
    string? DisplayName,
    string? EntraObjectId,
    IReadOnlyList<PortalRole> Roles);

public sealed record UpdatePortalUserRequest(
    IReadOnlyList<PortalRole> Roles,
    bool Disabled);
