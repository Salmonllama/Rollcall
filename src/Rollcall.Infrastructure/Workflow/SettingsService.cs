// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure.Workflow;

/// <summary>
/// Manages portal settings, feature toggles, approval rules, welcome pack branding, and hybrid AD settings.
/// </summary>
public sealed class SettingsService(RollcallDbContext db, TimeProvider clock)
{
    public const string KeySetupCompleted = "setup.completed";
    public const string KeyPortalSettings = "portal.settings";
    public const string KeyAdSettings = "ad.settings";
    public const string KeyWelcomePackSettings = "welcomePack.settings";
    public const string KeyWelcomePackHeaderBytes = "welcomePack.headerImage";
    public const string KeyWelcomePackLetterheadBytes = "welcomePack.letterhead";
    public const string KeyLastBackupAt = "ops.lastBackupAt";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<bool> IsSetupCompletedAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeySetupCompleted, cancellationToken);
        return row is not null && string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task MarkSetupCompletedAsync(CancellationToken cancellationToken = default)
    {
        await UpsertSettingAsync(KeySetupCompleted, "true", cancellationToken);
    }

    public async Task<PortalSettingsDto> GetPortalSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyPortalSettings, cancellationToken);
        PortalSettingsRecord model;
        if (row is null)
        {
            model = PortalSettingsRecord.Default();
        }
        else
        {
            model = JsonSerializer.Deserialize<PortalSettingsRecord>(row.Value, JsonOptions) ?? PortalSettingsRecord.Default();
        }

        var features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in FeatureFlagKeys.All)
        {
            features[key] = !model.Features.TryGetValue(key, out var val) || val;
        }

        var canonical = JsonSerializer.Serialize(model, JsonOptions);
        var etag = ComputeETag(canonical);
        return new PortalSettingsDto(
            model.TenantTimeZone,
            model.DefaultScheduleTimeLocal,
            model.DefaultUsageLocation,
            features,
            model.BreakGlassAccountIds,
            model.BreakGlassGroupIds,
            model.RunPayloadRetentionDays,
            etag);
    }

    public async Task<PortalSettingsDto> SavePortalSettingsAsync(
        UpdatePortalSettingsRequest request,
        string? expectedETag,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPortalSettingsAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(expectedETag) && !string.Equals(expectedETag.Trim('"'), current.ETag, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Settings were modified by another admin. Reload and try again.");
        }

        var record = new PortalSettingsRecord(
            string.IsNullOrWhiteSpace(request.TenantTimeZone) ? "UTC" : request.TenantTimeZone.Trim(),
            string.IsNullOrWhiteSpace(request.DefaultScheduleTimeLocal) ? "06:00" : request.DefaultScheduleTimeLocal.Trim(),
            string.IsNullOrWhiteSpace(request.DefaultUsageLocation) ? "US" : request.DefaultUsageLocation.Trim().ToUpperInvariant(),
            new Dictionary<string, bool>(request.Features, StringComparer.OrdinalIgnoreCase),
            request.BreakGlassAccountIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            request.BreakGlassGroupIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            request.RunPayloadRetentionDays);

        await UpsertSettingAsync(KeyPortalSettings, JsonSerializer.Serialize(record, JsonOptions), cancellationToken);
        return await GetPortalSettingsAsync(cancellationToken);
    }

    public async Task<AdSettingsDto> GetAdSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyAdSettings, cancellationToken);
        if (row is null)
        {
            return new AdSettingsDto([], null, null, null, 45, 60);
        }

        return JsonSerializer.Deserialize<AdSettingsDto>(row.Value, JsonOptions)
            ?? new AdSettingsDto([], null, null, null, 45, 60);
    }

    public async Task<AdSettingsDto> SaveAdSettingsAsync(AdSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var normalized = settings with
        {
            SyncWaitTimeoutMinutes = Math.Clamp(settings.SyncWaitTimeoutMinutes, 1, 240),
            AgentOfflineTimeoutMinutes = Math.Clamp(settings.AgentOfflineTimeoutMinutes, 1, 1440),
        };
        await UpsertSettingAsync(KeyAdSettings, JsonSerializer.Serialize(normalized, JsonOptions), cancellationToken);
        return normalized;
    }

    public async Task<WelcomePackSettingsDto> GetWelcomePackSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyWelcomePackSettings, cancellationToken);
        if (row is null)
        {
            return DefaultWelcomePackSettings();
        }

        return JsonSerializer.Deserialize<WelcomePackSettingsDto>(row.Value, JsonOptions)
            ?? DefaultWelcomePackSettings();
    }

    public async Task<WelcomePackSettingsDto> SaveWelcomePackSettingsAsync(
        WelcomePackSettingsDto settings,
        CancellationToken cancellationToken = default)
    {
        await UpsertSettingAsync(KeyWelcomePackSettings, JsonSerializer.Serialize(settings, JsonOptions), cancellationToken);
        return settings;
    }

    public async Task SaveWelcomePackAssetAsync(
        string assetKind,
        byte[] bytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var key = assetKind == "letterhead" ? KeyWelcomePackLetterheadBytes : KeyWelcomePackHeaderBytes;
        await UpsertSettingAsync(key, Convert.ToBase64String(bytes), cancellationToken);

        var current = await GetWelcomePackSettingsAsync(cancellationToken);
        var updated = assetKind == "letterhead"
            ? current with { HasLetterhead = bytes.Length > 0, LetterheadContentType = contentType }
            : current with { HasHeaderImage = bytes.Length > 0, HeaderImageContentType = contentType };
        await SaveWelcomePackSettingsAsync(updated, cancellationToken);
    }

    public async Task<byte[]?> GetWelcomePackAssetBytesAsync(string assetKind, CancellationToken cancellationToken = default)
    {
        var key = assetKind == "letterhead" ? KeyWelcomePackLetterheadBytes : KeyWelcomePackHeaderBytes;
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        return row is null || string.IsNullOrWhiteSpace(row.Value) ? null : Convert.FromBase64String(row.Value);
    }

    public async Task<DateTimeOffset?> GetLastBackupAtAsync(CancellationToken cancellationToken = default)
    {
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyLastBackupAt, cancellationToken);
        if (row is not null && DateTimeOffset.TryParse(row.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        var markerFile = "/var/lib/rollcall/last-backup.txt";
        if (File.Exists(markerFile))
        {
            var text = (await File.ReadAllTextAsync(markerFile, cancellationToken)).Trim();
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var fileParsed))
            {
                return fileParsed;
            }
        }

        return null;
    }

    public async Task RecordBackupTimestampAsync(DateTimeOffset timestamp, CancellationToken cancellationToken = default)
    {
        await UpsertSettingAsync(KeyLastBackupAt, timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), cancellationToken);
    }

    public async Task<IReadOnlyList<ApprovalRuleDto>> GetApprovalRulesAsync(CancellationToken cancellationToken = default)
    {
        var saved = (await db.ApprovalRules.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(r => r.ActionType);

        var list = new List<ApprovalRuleDto>();
        foreach (var spec in DefaultApprovalSpecs)
        {
            var hasSaved = saved.TryGetValue(spec.ActionType, out var entity);
            var required = spec.LockedRequired || (hasSaved ? entity!.Required : spec.DefaultRequired);
            var role = hasSaved ? entity!.ApproverRole : spec.DefaultRole;
            list.Add(new ApprovalRuleDto(spec.ActionType, spec.Label, spec.Description, required, role, spec.LockedRequired));
        }

        return list;
    }

    public async Task<ApprovalRuleDto> UpdateApprovalRuleAsync(
        WorkflowRunType actionType,
        UpdateApprovalRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var spec = DefaultApprovalSpecs.FirstOrDefault(s => s.ActionType == actionType)
            ?? throw new InvalidOperationException($"Unknown approval rule action type {actionType}.");

        if (spec.LockedRequired && !request.Required)
        {
            throw new InvalidOperationException("Conditional Access policy changes always require approval and cannot be turned off.");
        }

        var entity = await db.ApprovalRules.FirstOrDefaultAsync(r => r.ActionType == actionType, cancellationToken);
        if (entity is null)
        {
            entity = new ApprovalRuleEntity { ActionType = actionType };
            db.ApprovalRules.Add(entity);
        }

        entity.Required = spec.LockedRequired || request.Required;
        entity.ApproverRole = request.ApproverRole;
        entity.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        return new ApprovalRuleDto(actionType, spec.Label, spec.Description, entity.Required, entity.ApproverRole, spec.LockedRequired);
    }

    public async Task<(bool Required, PortalRole ApproverRole)> EvaluateApprovalRequirementAsync(
        WorkflowRunType actionType,
        bool onboardingHasAdminRoles,
        CancellationToken cancellationToken = default)
    {
        if (actionType == WorkflowRunType.ConditionalAccessEdit)
        {
            return (true, PortalRole.Admin);
        }

        var rules = await GetApprovalRulesAsync(cancellationToken);
        var rule = rules.FirstOrDefault(r => r.ActionType == actionType);
        var required = rule?.Required ?? false;
        if (actionType == WorkflowRunType.Onboarding && onboardingHasAdminRoles)
        {
            required = true;
        }

        return (required, rule?.ApproverRole ?? PortalRole.Approver);
    }

    private async Task UpsertSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        var entity = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (entity is null)
        {
            db.Settings.Add(new AppSettingEntity
            {
                Key = key,
                Value = value,
                UpdatedAt = clock.GetUtcNow(),
            });
        }
        else
        {
            entity.Value = value;
            entity.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeETag(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    private static WelcomePackSettingsDto DefaultWelcomePackSettings() => new(
        IntroText: "Welcome to the team. Use the credentials below to sign in to Microsoft 365 for the first time and register your multi-factor authentication method.",
        FirstSignInSteps: "1. Go to https://myaccount.microsoft.com.\n2. Enter your work username shown above.\n3. Enter the Temporary Access Pass or one-time password.",
        MfaSetupSteps: "1. Open Security info from your Microsoft account.\n2. Select Add sign-in method and choose Microsoft Authenticator or a security key.\n3. Follow the prompts to finish registration.",
        HideRollcallBranding: false,
        OrganizationName: null,
        HasHeaderImage: false,
        HeaderImageContentType: null,
        HasLetterhead: false,
        LetterheadContentType: null,
        ContentMarginTopPt: 72,
        ContentMarginRightPt: 54,
        ContentMarginBottomPt: 54,
        ContentMarginLeftPt: 54);

    private sealed record PortalSettingsRecord(
        string TenantTimeZone,
        string DefaultScheduleTimeLocal,
        string DefaultUsageLocation,
        Dictionary<string, bool> Features,
        List<string> BreakGlassAccountIds,
        List<string> BreakGlassGroupIds,
        int? RunPayloadRetentionDays)
    {
        public static PortalSettingsRecord Default() => new(
            "UTC",
            "06:00",
            "US",
            FeatureFlagKeys.All.ToDictionary(k => k, _ => true, StringComparer.OrdinalIgnoreCase),
            [],
            [],
            null);
    }

    private sealed record ApprovalSpec(
        WorkflowRunType ActionType,
        string Label,
        string Description,
        bool DefaultRequired,
        PortalRole DefaultRole,
        bool LockedRequired);

    private static readonly IReadOnlyList<ApprovalSpec> DefaultApprovalSpecs =
    [
        new(WorkflowRunType.Offboarding, "Offboarding", "Require a second person before blocking sign-in, converting mailboxes, and removing licenses.", true, PortalRole.Approver, false),
        new(WorkflowRunType.ConditionalAccessEdit, "Conditional Access changes", "Always require approval before changing policy include or exclude targets.", true, PortalRole.Admin, true),
        new(WorkflowRunType.Onboarding, "Onboarding", "Require approval for standard onboardings. Templates that assign admin roles always require approval.", false, PortalRole.Approver, false),
        new(WorkflowRunType.RestoreUser, "Restore offboarded user", "Require approval before reversing a completed offboarding.", true, PortalRole.Approver, false),
        new(WorkflowRunType.AttributeUpdate, "User attribute edits", "Require approval for single-user attribute changes.", false, PortalRole.Approver, false),
        new(WorkflowRunType.BulkAttributeUpdate, "Bulk attribute edits", "Require approval for multi-user attribute updates.", false, PortalRole.Approver, false),
        new(WorkflowRunType.GroupMembership, "Group membership changes", "Require approval for security, Microsoft 365, and distribution list membership edits.", false, PortalRole.Approver, false),
        new(WorkflowRunType.LicenseAssignment, "License changes", "Require approval for license assignments and removals.", false, PortalRole.Approver, false),
        new(WorkflowRunType.MfaAction, "MFA and Temporary Access Pass", "Require approval for MFA method removal, re-registration, or TAP issuance.", false, PortalRole.Approver, false),
        new(WorkflowRunType.PasswordReset, "Password reset", "Require approval before resetting a user's password.", false, PortalRole.Approver, false),
        new(WorkflowRunType.SessionRevoke, "Session revocation", "Require approval before signing a user out of active sessions.", false, PortalRole.Approver, false),
        new(WorkflowRunType.AdGroupManagement, "On-prem AD group changes", "Require approval before creating, moving, or editing on-prem AD groups.", false, PortalRole.Approver, false),
    ];
}
