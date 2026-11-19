// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record PortalSettingsDto(
    string TenantTimeZone,
    string DefaultScheduleTimeLocal,
    string DefaultUsageLocation,
    IReadOnlyDictionary<string, bool> Features,
    IReadOnlyList<string> BreakGlassAccountIds,
    IReadOnlyList<string> BreakGlassGroupIds,
    int? RunPayloadRetentionDays,
    string ETag);

public sealed record UpdatePortalSettingsRequest(
    string TenantTimeZone,
    string DefaultScheduleTimeLocal,
    string DefaultUsageLocation,
    IReadOnlyDictionary<string, bool> Features,
    IReadOnlyList<string> BreakGlassAccountIds,
    IReadOnlyList<string> BreakGlassGroupIds,
    int? RunPayloadRetentionDays);

public sealed record HealthComponentDto(
    string Name,
    string Status,
    string Summary,
    string? Detail);

public sealed record HealthOverviewDto(
    string OverallStatus,
    DateTimeOffset CheckedAt,
    string Version,
    int QueueDepth,
    int FailedRunsLast24Hours,
    DateTimeOffset? LastBackupAt,
    long? DiskFreeBytes,
    long? DiskTotalBytes,
    IReadOnlyList<HealthComponentDto> Components);
