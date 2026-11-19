// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using Rollcall.Contracts;
using Rollcall.Domain.Workflow;

namespace Rollcall.Domain.Policies;

/// <summary>Implements the portal role matrix from implementation-plan.md section 4.5.</summary>
public static class RoleAuthorizationPolicy
{
    public static bool CanViewPortal(IReadOnlyList<PortalRole> roles) => roles.Count > 0;

    public static bool CanRunStandardWorkflows(IReadOnlyList<PortalRole> roles) =>
        roles.Contains(PortalRole.Technician) || roles.Contains(PortalRole.Admin);

    public static bool CanApprove(IReadOnlyList<PortalRole> roles) =>
        roles.Contains(PortalRole.Approver) || roles.Contains(PortalRole.Admin);

    public static bool CanManageConditionalAccess(IReadOnlyList<PortalRole> roles) =>
        roles.Contains(PortalRole.Admin);

    public static bool CanAdminister(IReadOnlyList<PortalRole> roles) =>
        roles.Contains(PortalRole.Admin);

    public static bool CanViewAllAuditEvents(IReadOnlyList<PortalRole> roles) =>
        roles.Contains(PortalRole.Approver) || roles.Contains(PortalRole.Admin);

    public static bool CanApproveRun(
        IReadOnlyList<PortalRole> actorRoles,
        string actorUpn,
        string requesterUpn,
        PortalRole requiredApproverRole)
    {
        if (string.Equals(actorUpn, requesterUpn, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return requiredApproverRole == PortalRole.Admin
            ? actorRoles.Contains(PortalRole.Admin)
            : CanApprove(actorRoles);
    }
}

/// <summary>Implements cloud vs hybrid user routing from implementation-plan.md section 4.8.</summary>
public static class HybridRoutingPolicy
{
    public static readonly IReadOnlySet<string> AdMasteredAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "displayName",
        "givenName",
        "surname",
        "jobTitle",
        "department",
        "manager",
        "officeLocation",
        "businessPhone",
        "mobilePhone",
        "employeeId",
        "companyName",
    };

    public static readonly IReadOnlySet<string> CloudOnlyAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "usageLocation",
    };

    public static bool IsAdMasteredAttribute(string attributeName) =>
        AdMasteredAttributes.Contains(attributeName);

    public static string? ValidateGraphAttributeEdit(
        bool onPremisesSyncEnabled,
        string attributeName,
        bool hybridAdEnabled)
    {
        if (!onPremisesSyncEnabled || !IsAdMasteredAttribute(attributeName))
        {
            return null;
        }

        if (!hybridAdEnabled)
        {
            return $"Can't edit {attributeName} through Microsoft Graph because this user is synced from on-prem Active Directory. Enable the Hybrid AD agent in Settings to manage AD-mastered attributes.";
        }

        return null;
    }

    public static TargetSystem ResolveAttributeTarget(bool onPremisesSyncEnabled, string attributeName) =>
        onPremisesSyncEnabled && IsAdMasteredAttribute(attributeName)
            ? TargetSystem.ActiveDirectory
            : TargetSystem.EntraId;
}

/// <summary>Evaluates whether each Conditional Access policy applies to a user (M4 criterion 4).</summary>
public static class CaApplicabilityEvaluator
{
    public static CaPolicyApplicabilityDto Evaluate(
        ConditionalAccessPolicyDto policy,
        string userId,
        IReadOnlySet<string> userGroupIds,
        IReadOnlySet<string> userRoleIds)
    {
        var tempExpiry = policy.ActiveTemporaryExclusions
            .Where(r => !r.Executed && string.Equals(r.TargetRef, userId, StringComparison.OrdinalIgnoreCase))
            .Select(r => (DateTimeOffset?)r.DueAt)
            .FirstOrDefault();

        if (string.Equals(policy.State, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return new CaPolicyApplicabilityDto(
                policy.Id,
                policy.DisplayName,
                policy.State,
                "Does not apply",
                "Policy is turned off.",
                tempExpiry);
        }

        if (policy.ExcludeUsers.Contains(userId, StringComparer.OrdinalIgnoreCase))
        {
            return new CaPolicyApplicabilityDto(
                policy.Id,
                policy.DisplayName,
                policy.State,
                "Excluded",
                tempExpiry.HasValue
                    ? "Temporarily excluded directly."
                    : "Excluded directly from this policy.",
                tempExpiry);
        }

        var excludedGroup = policy.ExcludeGroups.FirstOrDefault(g => userGroupIds.Contains(g));
        if (excludedGroup is not null)
        {
            return new CaPolicyApplicabilityDto(
                policy.Id,
                policy.DisplayName,
                policy.State,
                "Excluded",
                $"Excluded via group {excludedGroup}.",
                tempExpiry);
        }

        var excludedRole = policy.ExcludeRoles.FirstOrDefault(r => userRoleIds.Contains(r));
        if (excludedRole is not null)
        {
            return new CaPolicyApplicabilityDto(
                policy.Id,
                policy.DisplayName,
                policy.State,
                "Excluded",
                $"Excluded via directory role {excludedRole}.",
                tempExpiry);
        }

        var directInclude = policy.IncludeUsers.Contains(userId, StringComparer.OrdinalIgnoreCase);
        var allUsersInclude = policy.IncludeUsers.Contains("All", StringComparer.OrdinalIgnoreCase);
        var includedGroup = policy.IncludeGroups.FirstOrDefault(g => userGroupIds.Contains(g));
        var includedRole = policy.IncludeRoles.FirstOrDefault(r => userRoleIds.Contains(r));

        if (!directInclude && !allUsersInclude && includedGroup is null && includedRole is null)
        {
            var hasUncertainTargets = policy.IncludeRoles.Count > 0 ||
                policy.IncludeUsers.Contains("GuestsOrExternalUsers", StringComparer.OrdinalIgnoreCase);
            return new CaPolicyApplicabilityDto(
                policy.Id,
                policy.DisplayName,
                policy.State,
                hasUncertainTargets ? "May apply" : "Does not apply",
                hasUncertainTargets
                    ? "Policy targets directory roles or guest scopes that depend on runtime sign-in conditions."
                    : "User is not included directly or through a group.",
                tempExpiry);
        }

        var hasExtraConditions = !string.IsNullOrWhiteSpace(policy.ConditionsSummary) &&
            !string.Equals(policy.ConditionsSummary, "All cloud apps", StringComparison.OrdinalIgnoreCase);

        var baseReason = directInclude
            ? "Included directly."
            : includedGroup is not null
                ? $"Included via group {includedGroup}."
                : includedRole is not null
                    ? $"Included via directory role {includedRole}."
                    : "Included via All users.";

        var applicability = string.Equals(policy.State, "enabledForReportingButNotEnforced", StringComparison.OrdinalIgnoreCase)
            ? "Report-only"
            : hasExtraConditions
                ? "May apply"
                : "Applies";

        return new CaPolicyApplicabilityDto(
            policy.Id,
            policy.DisplayName,
            policy.State,
            applicability,
            hasExtraConditions ? $"{baseReason} Depends on policy conditions ({policy.ConditionsSummary})." : baseReason,
            tempExpiry);
    }
}

public sealed record CaSafetyEvaluation(
    IReadOnlyList<string> BlockingErrors,
    IReadOnlyList<string> Warnings,
    bool RequiresApproval);

/// <summary>Evaluates Conditional Access safety rails at plan time (M8).</summary>
public static class CaSafetyRailEvaluator
{
    public static CaSafetyEvaluation Evaluate(
        ConditionalAccessPolicyDto currentPolicy,
        IReadOnlyList<string> newIncludeUsers,
        IReadOnlyList<string> newExcludeUsers,
        IReadOnlyList<string> newIncludeGroups,
        IReadOnlyList<string> newExcludeGroups,
        IReadOnlyList<string> breakGlassAccountIds,
        IReadOnlyList<string> breakGlassGroupIds)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (newIncludeUsers.Count == 0 && newIncludeGroups.Count == 0 && currentPolicy.IncludeRoles.Count == 0)
        {
            errors.Add("Can't save a Conditional Access policy with no included users, groups, or roles.");
        }

        foreach (var bgUser in breakGlassAccountIds)
        {
            var wasExcluded = currentPolicy.ExcludeUsers.Contains(bgUser, StringComparer.OrdinalIgnoreCase);
            var stillExcluded = newExcludeUsers.Contains(bgUser, StringComparer.OrdinalIgnoreCase);
            if (wasExcluded && !stillExcluded)
            {
                errors.Add($"Can't remove break-glass account {bgUser} from policy exclusions.");
            }
        }

        foreach (var bgGroup in breakGlassGroupIds)
        {
            var wasExcluded = currentPolicy.ExcludeGroups.Contains(bgGroup, StringComparer.OrdinalIgnoreCase);
            var stillExcluded = newExcludeGroups.Contains(bgGroup, StringComparer.OrdinalIgnoreCase);
            if (wasExcluded && !stillExcluded)
            {
                errors.Add($"Can't remove break-glass group {bgGroup} from policy exclusions.");
            }
        }

        var newlyExcludedUsers = newExcludeUsers
            .Except(currentPolicy.ExcludeUsers, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var newlyExcludedGroups = newExcludeGroups
            .Except(currentPolicy.ExcludeGroups, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if ((newlyExcludedUsers.Count > 0 || newlyExcludedGroups.Count > 0) &&
            (currentPolicy.RequiresMfa || currentPolicy.BlocksLegacyAuth))
        {
            var protection = currentPolicy.RequiresMfa && currentPolicy.BlocksLegacyAuth
                ? "requires MFA and blocks legacy authentication"
                : currentPolicy.RequiresMfa
                    ? "requires MFA"
                    : "blocks legacy authentication";
            warnings.Add($"Excluding targets from \"{currentPolicy.DisplayName}\" bypasses a policy that {protection}.");
        }

        var isEnabled = string.Equals(currentPolicy.State, "enabled", StringComparison.OrdinalIgnoreCase);
        return new CaSafetyEvaluation(errors, warnings, RequiresApproval: isEnabled || true);
    }
}

/// <summary>Pattern resolution and collision checks for onboarding (M6).</summary>
public static class OnboardingDomainLogic
{
    public static string RenderPattern(string pattern, string firstName, string lastName, string domain)
    {
        var cleanFirst = SanitizeToken(firstName);
        var cleanLast = SanitizeToken(lastName);
        var firstInitial = cleanFirst.Length > 0 ? cleanFirst[..1] : string.Empty;
        var lastInitial = cleanLast.Length > 0 ? cleanLast[..1] : string.Empty;

        var isUpnPattern = pattern.Contains('@', StringComparison.Ordinal);
        if (isUpnPattern)
        {
            return pattern
                .Replace("{first}", cleanFirst.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                .Replace("{last}", cleanLast.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                .Replace("{fi}", firstInitial.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                .Replace("{li}", lastInitial.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                .Replace("{domain}", domain.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        return pattern
            .Replace("{first}", firstName.Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{last}", lastName.Trim(), StringComparison.OrdinalIgnoreCase)
            .Replace("{fi}", firstInitial.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace("{li}", lastInitial.ToUpperInvariant(), StringComparison.OrdinalIgnoreCase)
            .Replace("{domain}", domain.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string DeriveMailNickname(string upn)
    {
        var at = upn.IndexOf('@', StringComparison.Ordinal);
        return at > 0 ? upn[..at] : upn;
    }

    public static string DeriveSamAccountName(string mailNickname)
    {
        var sb = new StringBuilder(20);
        foreach (var ch in mailNickname)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')
            {
                sb.Append(ch);
            }

            if (sb.Length == 20)
            {
                break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Detects collisions without ever proposing an alternative value (M6 criterion 3).
    /// </summary>
    public static IReadOnlyList<OnboardingCollisionDto> DetectCollisions(
        string proposedUpn,
        string proposedMailNickname,
        string proposedDisplayName,
        string? proposedSamAccountName,
        IReadOnlyList<DirectoryUserDto> existingUsers)
    {
        var collisions = new List<OnboardingCollisionDto>();

        foreach (var existing in existingUsers)
        {
            if (string.Equals(existing.UserPrincipalName, proposedUpn, StringComparison.OrdinalIgnoreCase))
            {
                collisions.Add(new OnboardingCollisionDto(
                    "userPrincipalName",
                    proposedUpn,
                    existing.DisplayName,
                    existing.UserPrincipalName,
                    TargetSystem.EntraId));
            }

            var existingNick = DeriveMailNickname(existing.Mail ?? existing.UserPrincipalName);
            if (string.Equals(existingNick, proposedMailNickname, StringComparison.OrdinalIgnoreCase))
            {
                collisions.Add(new OnboardingCollisionDto(
                    "mailNickname",
                    proposedMailNickname,
                    existing.DisplayName,
                    existing.UserPrincipalName,
                    TargetSystem.EntraId));
            }

            if (string.Equals(existing.DisplayName, proposedDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                collisions.Add(new OnboardingCollisionDto(
                    "displayName",
                    proposedDisplayName,
                    existing.DisplayName,
                    existing.UserPrincipalName,
                    TargetSystem.EntraId));
            }

            if (!string.IsNullOrWhiteSpace(proposedSamAccountName) &&
                !string.IsNullOrWhiteSpace(existing.OnPremisesSamAccountName) &&
                string.Equals(existing.OnPremisesSamAccountName, proposedSamAccountName, StringComparison.OrdinalIgnoreCase))
            {
                collisions.Add(new OnboardingCollisionDto(
                    "sAMAccountName",
                    proposedSamAccountName,
                    existing.DisplayName,
                    existing.OnPremisesSamAccountName,
                    TargetSystem.ActiveDirectory));
            }
        }

        return collisions;
    }

    private static string SanitizeToken(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }
}

/// <summary>Enforces M7 offboarding step ordering and safety checks.</summary>
public static class OffboardingDomainLogic
{
    public static readonly IReadOnlySet<string> PrivilegedDirectoryRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Global Administrator",
        "Privileged Role Administrator",
        "Security Administrator",
        "Exchange Administrator",
        "User Administrator",
        "Authentication Administrator",
        "Privileged Authentication Administrator",
        "Conditional Access Administrator",
        "SharePoint Administrator",
    };

    public const double SharedMailboxFreeLimitGb = 50.0;

    /// <summary>
    /// Validates that license removal is never scheduled before shared-mailbox conversion (M7 criterion 1).
    /// </summary>
    public static void AssertValidStepOrder(IReadOnlyList<string> orderedStepKinds)
    {
        var convertIdx = -1;
        var licenseRemoveIdx = -1;
        var blockIdx = -1;
        var revokeIdx = -1;

        for (var i = 0; i < orderedStepKinds.Count; i++)
        {
            var kind = orderedStepKinds[i];
            if (kind is "graph.user.setAccountEnabled" or "ad.user.setEnabled" && blockIdx == -1)
            {
                blockIdx = i;
            }
            else if (kind == "graph.user.revokeSessions" && revokeIdx == -1)
            {
                revokeIdx = i;
            }
            else if (kind == "exchange.mailbox.convertToShared")
            {
                convertIdx = i;
            }
            else if (kind == "graph.license.remove" && licenseRemoveIdx == -1)
            {
                licenseRemoveIdx = i;
            }
        }

        if (convertIdx >= 0 && licenseRemoveIdx >= 0 && licenseRemoveIdx < convertIdx)
        {
            throw new InvalidOperationException(
                "Invalid offboarding plan order: license removal must never run before shared-mailbox conversion is verified.");
        }

        if (blockIdx >= 0 && licenseRemoveIdx >= 0 && licenseRemoveIdx < blockIdx)
        {
            throw new InvalidOperationException(
                "Invalid offboarding plan order: sign-in must be blocked before removing licenses.");
        }
    }

    public static (IReadOnlyList<string> Warnings, IReadOnlyList<string> BlockingErrors) EvaluatePreflight(
        double? mailboxSizeGb,
        bool litigationHoldEnabled,
        IReadOnlyList<string> ownedGroupNames,
        IReadOnlyList<string> userDirectoryRoles,
        IReadOnlyList<PortalRole> actorRoles,
        bool confirmPrivilegedRoleAsAdmin)
    {
        var warnings = new List<string>();
        var blocking = new List<string>();

        if (mailboxSizeGb.HasValue && mailboxSizeGb.Value > SharedMailboxFreeLimitGb)
        {
            warnings.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Mailbox is {mailboxSizeGb.Value:0.0} GB, which exceeds the 50 GB limit for an unlicensed shared mailbox. Keep an Exchange Online Plan 2 license or reduce mailbox size before removing licenses."));
        }

        if (litigationHoldEnabled)
        {
            warnings.Add("Mailbox has litigation hold enabled. Converting to shared or removing licenses may fail or require a retention license.");
        }

        if (ownedGroupNames.Count > 0)
        {
            warnings.Add($"User owns {ownedGroupNames.Count} group(s) or Team(s): {string.Join(", ", ownedGroupNames)}. Assign a new owner.");
        }

        var heldPrivileged = userDirectoryRoles
            .Where(r => PrivilegedDirectoryRoles.Contains(r))
            .ToList();

        if (heldPrivileged.Count > 0)
        {
            var list = string.Join(", ", heldPrivileged);
            if (!actorRoles.Contains(PortalRole.Admin))
            {
                blocking.Add($"You need the Admin role to offboard a user holding privileged directory roles ({list}).");
            }
            else if (!confirmPrivilegedRoleAsAdmin)
            {
                warnings.Add($"This user holds privileged directory roles ({list}). Confirm as an Admin before running offboarding.");
            }
        }

        return (warnings, blocking);
    }
}

/// <summary>Builds the live permission manifest and Markdown reference from step metadata (4.6).</summary>
public static class PermissionManifestBuilder
{
    public static readonly IReadOnlyList<RequiredPermission> BaselineReadPermissions =
    [
        new("Microsoft Graph", "User.Read.All", null, "core", "Read user profiles, status, and managers"),
        new("Microsoft Graph", "Group.Read.All", null, "core", "Read security and Microsoft 365 groups and memberships"),
        new("Microsoft Graph", "Organization.Read.All", null, "core", "Read tenant domain, name, and subscribed SKUs"),
        new("Microsoft Graph", "Directory.Read.All", null, "core", "Read directory roles and service principal assignments"),
        new("Microsoft Graph", "AuditLog.Read.All", null, "core", "Read sign-in audit summaries (optional)"),
    ];

    public static PermissionManifestDto Build(
        IEnumerable<IWorkflowStep> steps,
        IReadOnlyDictionary<string, bool> featureFlags)
    {
        var all = BaselineReadPermissions
            .Concat(steps.SelectMany(s => s.Permissions))
            .GroupBy(p => (p.Api, p.Permission, p.DirectoryRole, p.Feature))
            .Select(g => g.First())
            .OrderBy(p => p.Feature, StringComparer.Ordinal)
            .ThenBy(p => p.Api, StringComparer.Ordinal)
            .ThenBy(p => p.Permission, StringComparer.Ordinal)
            .Select(p =>
            {
                var enabled = string.Equals(p.Feature, "core", StringComparison.OrdinalIgnoreCase)
                    || !featureFlags.TryGetValue(p.Feature, out var flag)
                    || flag;
                return new PermissionManifestEntryDto(
                    p.Api,
                    p.Permission,
                    p.DirectoryRole,
                    p.Feature,
                    p.Description,
                    enabled);
            })
            .ToList();

        var enabledFeatures = featureFlags.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return new PermissionManifestDto(all, enabledFeatures);
    }

    public static string RenderMarkdown(PermissionManifestDto manifest)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Permissions reference");
        sb.AppendLine();
        sb.AppendLine("This file is generated from Rollcall's permission manifest. Every workflow step declares the Microsoft Graph application permissions, Exchange Online permissions, and Entra ID directory roles it requires. Turning a feature off in **Settings** drops its permissions from this manifest and from the setup script.");
        sb.AppendLine();
        sb.AppendLine("## Application permissions and directory roles");
        sb.AppendLine();
        sb.AppendLine("| Feature | API | Application permission | Directory role | Used for |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");

        foreach (var entry in manifest.Entries)
        {
            var role = string.IsNullOrWhiteSpace(entry.DirectoryRole) ? "None" : $"`{entry.DirectoryRole}`";
            sb.AppendLine(CultureInfo.InvariantCulture, $"| `{entry.Feature}` | {entry.Api} | `{entry.Permission}` | {role} | {entry.Description} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Checking permissions in Rollcall");
        sb.AppendLine();
        sb.AppendLine("Open **Admin › Connections** to compare the permissions granted to your app registration against the features currently enabled. Missing permissions list the exact features affected.");
        return sb.ToString();
    }
}

/// <summary>Maps raw Graph/Exchange/AD failures into calm, actionable Rollcall copy (brand guide §11).</summary>
public static class PlainLanguageErrorMapper
{
    public static string MapGraphError(string actionDescription, int statusCode, string? rawMessage)
    {
        return statusCode switch
        {
            403 => $"Couldn't {actionDescription}. Microsoft Graph denied permission. Check Admin › Connections for missing permissions or directory roles, then retry.",
            404 => $"Couldn't {actionDescription}. Microsoft Graph couldn't find the target object. Confirm it still exists in Entra ID, then retry.",
            409 => $"Couldn't {actionDescription}. A conflicting object already exists in Entra ID. Review the conflict and update the input before retrying.",
            429 => $"Microsoft Graph throttled the request while trying to {actionDescription}. Rollcall will retry automatically after the wait period.",
            >= 500 => $"Microsoft Graph returned a temporary server error while trying to {actionDescription}. Rollcall will retry automatically.",
            _ => string.IsNullOrWhiteSpace(rawMessage)
                ? $"Couldn't {actionDescription}. Review the raw error details below, fix the issue, and retry."
                : $"Couldn't {actionDescription}. Microsoft Graph reported: {rawMessage}",
        };
    }

    public static string MapExchangeError(string actionDescription, string rawMessage)
    {
        if (rawMessage.Contains("litigation hold", StringComparison.OrdinalIgnoreCase))
        {
            return $"Couldn't {actionDescription}. Exchange Online reported that the mailbox is on litigation hold. Remove the hold or skip this step, then retry.";
        }

        if (rawMessage.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            rawMessage.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase))
        {
            return $"Couldn't {actionDescription}. Exchange Online denied access. Verify Exchange.ManageAsApp and the Exchange Administrator directory role in Admin › Connections, then retry.";
        }

        return $"Couldn't {actionDescription}. Exchange Online reported: {rawMessage}";
    }
}
