// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record SetupStatusDto(
    bool IsCompleted,
    bool HasTokenSession,
    bool HasCertificate,
    bool HasTenantConnection,
    string? Hostname,
    string? RedirectUri,
    AppCertificateSummaryDto? PendingCertificate,
    string? ConnectedTenantId,
    string? ConnectedClientId,
    string? PrimaryDomain);

public sealed record VerifySetupTokenRequest(string Token);

public sealed record AppCertificateSummaryDto(
    Guid Id,
    string Thumbprint,
    string Subject,
    string Algorithm,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    AppCertificateStatus Status,
    int DaysUntilExpiry);

public sealed record AppRegistrationGuideDto(
    string Hostname,
    string RedirectUri,
    string CertificateThumbprint,
    string PowerShellScript,
    IReadOnlyList<PermissionManifestEntryDto> Permissions,
    IReadOnlyList<string> DirectoryRoles);

public sealed record PermissionManifestEntryDto(
    string Api,
    string Permission,
    string? DirectoryRole,
    string Feature,
    string Description,
    bool IsEnabled);

public sealed record PermissionManifestDto(
    IReadOnlyList<PermissionManifestEntryDto> Entries,
    IReadOnlyList<string> EnabledFeatures);

public sealed record ValidateConnectionRequest(string TenantId, string ClientId, string? PrimaryDomain);

public sealed record PermissionCheckItemDto(
    string Name,
    string Api,
    string Kind,
    bool Granted,
    IReadOnlyList<string> AffectedFeatures);

public sealed record ConnectionValidationResultDto(
    bool Succeeded,
    string? TenantId,
    string? DisplayName,
    string? PrimaryDomain,
    string? Error,
    IReadOnlyList<PermissionCheckItemDto> Permissions,
    IReadOnlyList<PermissionCheckItemDto> DirectoryRoles);

public sealed record CompleteSetupRequest(string? FirstAdminUpn, string? FirstAdminObjectId, string? FirstAdminDisplayName);

public sealed record ConnectionsOverviewDto(
    string? TenantId,
    string? AppClientId,
    string? PrimaryDomain,
    string? OrganizationName,
    DateTimeOffset? ConnectedAt,
    AppCertificateSummaryDto? ActiveCertificate,
    AppCertificateSummaryDto? PendingCertificate,
    AppCertificateSummaryDto? RetiringCertificate,
    bool ShowExpiryWarning,
    IReadOnlyList<PermissionCheckItemDto> Permissions,
    IReadOnlyList<PermissionCheckItemDto> DirectoryRoles,
    string? LastCheckError);

public sealed record RotateCertificateStartResponse(
    AppCertificateSummaryDto PendingCertificate,
    string PowerShellScript,
    string DownloadCerUrl);

public sealed record RotateCertificateActionRequest(Guid CertificateId);
