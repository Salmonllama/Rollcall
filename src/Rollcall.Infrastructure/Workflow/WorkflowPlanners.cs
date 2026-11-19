// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Workflow.Steps;

namespace Rollcall.Infrastructure.Workflow;

/// <summary>
/// High-level workflow planners that assemble ordered PlannedStepSpecs for onboarding (M6),
/// offboarding & restore (M7), management actions (M5), Conditional Access (M8), and Hybrid AD (M9).
/// </summary>
public sealed class WorkflowPlanners(
    RollcallDbContext db,
    IGraphClient graph,
    IExchangeRunner exchange,
    SettingsService settingsService,
    WorkflowEngine engine,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<OnboardingPlanResponse> PlanOrSubmitOnboardingAsync(
        OnboardingRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var template = await db.RoleTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.RoleTemplateId && !t.IsArchived, cancellationToken)
            ?? throw new KeyNotFoundException($"Role template {request.RoleTemplateId} was not found.");

        var def = JsonSerializer.Deserialize<RoleTemplateDefinition>(template.DefinitionJson, JsonOptions)
            ?? throw new InvalidOperationException("Role template definition is invalid.");

        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var domain = tenant?.PrimaryDomain ?? "contoso.com";
        var portalSettings = await settingsService.GetPortalSettingsAsync(cancellationToken);
        var adSettings = await settingsService.GetAdSettingsAsync(cancellationToken);

        var proposedUpn = !string.IsNullOrWhiteSpace(request.CustomUpn)
            ? request.CustomUpn.Trim()
            : OnboardingDomainLogic.RenderPattern(def.UpnPattern, request.FirstName, request.LastName, domain);
        var proposedMailNickname = !string.IsNullOrWhiteSpace(request.CustomMailNickname)
            ? request.CustomMailNickname.Trim()
            : OnboardingDomainLogic.DeriveMailNickname(proposedUpn);
        var proposedDisplayName = !string.IsNullOrWhiteSpace(request.CustomDisplayName)
            ? request.CustomDisplayName.Trim()
            : OnboardingDomainLogic.RenderPattern(def.DisplayNamePattern, request.FirstName, request.LastName, domain);

        var isHybrid = string.Equals(def.AccountType, "hybrid", StringComparison.OrdinalIgnoreCase);
        var proposedSam = isHybrid
            ? (!string.IsNullOrWhiteSpace(request.CustomSamAccountName)
                ? request.CustomSamAccountName.Trim()
                : OnboardingDomainLogic.DeriveSamAccountName(proposedMailNickname))
            : null;

        // M6 criterion 3: check collisions against existing users without proposing an alternative.
        var existingUsers = await graph.SearchUsersAsync(null, null, null, null, null, 500, cancellationToken);
        var collisions = OnboardingDomainLogic.DetectCollisions(
            proposedUpn,
            proposedMailNickname,
            proposedDisplayName,
            proposedSam,
            existingUsers);

        var blockingErrors = collisions
            .Select(c => $"{c.Field} \"{c.ProposedValue}\" is already in use by {c.ConflictingObjectDisplayName} ({c.ConflictingObjectIdentifier}). Enter a different value to continue.")
            .ToList();

        var department = request.Department ?? def.Department;
        var jobTitle = request.JobTitle ?? def.JobTitle;
        var office = request.OfficeLocation ?? def.OfficeLocation;
        var usageLocation = request.UsageLocation ?? def.UsageLocation ?? portalSettings.DefaultUsageLocation;
        var now = clock.GetUtcNow();
        var isFutureStart = request.StartDate.HasValue && request.StartDate.Value > now;
        var createBlocked = isFutureStart && def.CreateBlockedUntilStartDate;

        var specs = new List<PlannedStepSpec>();

        if (isHybrid)
        {
            var targetOu = request.TargetOuOverride ?? def.TargetOu ?? adSettings.DefaultNewUserOu ?? "OU=Users,DC=corp,DC=local";
            specs.Add(Spec(AdJobKinds.UserCreate, new AdUserCreatePayload(
                proposedSam!,
                proposedUpn,
                proposedDisplayName,
                request.FirstName,
                request.LastName,
                targetOu,
                department,
                jobTitle,
                office,
                def.CompanyName,
                null,
                null,
                !createBlocked,
                EphemeralCredentialStore.GenerateStrongPassword())));

            foreach (var adGroup in def.AdGroupDns)
            {
                specs.Add(Spec(AdJobKinds.GroupAddMember, new AdGroupMembershipPayload(adGroup, proposedSam!)));
            }

            specs.Add(Spec(AdJobKinds.SyncDelta, new AdSyncDeltaPayload(adSettings.EntraConnectServer)));
            specs.Add(Spec("graph.user.waitForSync", new WaitForEntraSyncStepInput(proposedSam!, proposedUpn, adSettings.SyncWaitTimeoutMinutes)));
            if (!string.IsNullOrWhiteSpace(usageLocation))
            {
                specs.Add(Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(proposedUpn, "usageLocation", usageLocation)));
            }
        }
        else
        {
            var usePasswordMode = string.Equals(def.CredentialMode, "password", StringComparison.OrdinalIgnoreCase);
            specs.Add(Spec("graph.user.create", new CreateGraphUserStepInput(
                proposedDisplayName,
                proposedUpn,
                proposedMailNickname,
                request.FirstName,
                request.LastName,
                AccountEnabled: !createBlocked,
                ForceChangePasswordNextSignIn: true,
                Department: department,
                JobTitle: jobTitle,
                OfficeLocation: office,
                CompanyName: def.CompanyName,
                UsageLocation: usageLocation,
                ReturnPasswordAsEphemeral: usePasswordMode && !createBlocked)));
        }

        if (!string.IsNullOrWhiteSpace(request.ManagerId) && !isHybrid)
        {
            specs.Add(Spec("graph.user.setManager", new SetManagerStepInput(proposedUpn, request.ManagerId)));
        }

        foreach (var groupId in def.CloudGroupIds)
        {
            var gDetail = await graph.GetGroupDetailAsync(groupId, cancellationToken);
            specs.Add(Spec("graph.group.addMember", new GroupMemberStepInput(proposedUpn, groupId, gDetail?.Group.DisplayName ?? groupId)));
        }

        foreach (var dlId in def.DistributionListIds)
        {
            specs.Add(Spec("exchange.dl.addMember", new DistributionListMemberStepInput(dlId, dlId, proposedUpn)));
        }

        foreach (var lic in def.Licenses)
        {
            specs.Add(Spec("graph.license.assign", new AssignLicenseStepInput(proposedUpn, lic.SkuId, lic.DisabledPlans)));
        }

        if (!string.IsNullOrWhiteSpace(def.MailboxTimeZone) || !string.IsNullOrWhiteSpace(def.MailboxLanguage))
        {
            specs.Add(Spec("exchange.mailbox.setRegionalSettings", new MailboxRegionalSettingsStepInput(proposedUpn, def.MailboxTimeZone, def.MailboxLanguage)));
        }

        // If NOT creating blocked for future activation, issue TAP immediately if configured.
        var useTap = !string.Equals(def.CredentialMode, "password", StringComparison.OrdinalIgnoreCase);
        if (!createBlocked && useTap)
        {
            specs.Add(Spec("graph.mfa.issueTap", new IssueTapStepInput(proposedUpn, Math.Max(10, def.TapLifetimeMinutes), def.TapUsableOnce)));
        }

        if (!request.Submit || collisions.Count > 0)
        {
            var preview = await engine.PlanAsync(
                WorkflowRunType.Onboarding,
                specs,
                actor,
                onboardingHasAdminRoles: def.AssignsAdminRoles,
                extraBlockingErrors: blockingErrors,
                cancellationToken: cancellationToken);
            return new OnboardingPlanResponse(preview.Plan, collisions, null);
        }

        // When CreateBlockedUntilStartDate is true and StartDate is in the future:
        // Run 1 creates the blocked account immediately; Run 2 is scheduled for StartDate to enable sign-in and issue TAP (M6 criterion 4).
        var run = await engine.CreateRunAsync(
            WorkflowRunType.Onboarding,
            proposedUpn,
            proposedDisplayName,
            specs,
            actor,
            scheduledFor: createBlocked ? null : request.StartDate,
            requestPayload: request,
            roleTemplateId: template.Id,
            roleTemplateVersion: template.Version,
            onboardingHasAdminRoles: def.AssignsAdminRoles,
            cancellationToken: cancellationToken);

        if (createBlocked && request.StartDate.HasValue)
        {
            var activationSpecs = new List<PlannedStepSpec>
            {
                isHybrid
                    ? Spec(AdJobKinds.UserSetEnabled, new AdUserSetEnabledPayload(proposedSam!, true))
                    : Spec("graph.user.setAccountEnabled", new SetAccountEnabledStepInput(proposedUpn, true)),
            };
            if (useTap)
            {
                activationSpecs.Add(Spec("graph.mfa.issueTap", new IssueTapStepInput(proposedUpn, Math.Max(10, def.TapLifetimeMinutes), def.TapUsableOnce)));
            }

            await engine.CreateRunAsync(
                WorkflowRunType.Onboarding,
                proposedUpn,
                proposedDisplayName,
                activationSpecs,
                actor,
                scheduledFor: request.StartDate.Value,
                requestPayload: new { phase = "startDateActivation", parentRunId = run.Id, request.StartDate },
                roleTemplateId: template.Id,
                roleTemplateVersion: template.Version,
                onboardingHasAdminRoles: false,
                cancellationToken: cancellationToken);
        }

        var dto = engine.ToRunDto(run, actor);
        return new OnboardingPlanResponse(dto.Plan, [], dto);
    }

    public async Task<(PlanSummaryDto Plan, WorkflowRunDto? Run)> PlanOrSubmitOffboardingAsync(
        OffboardingRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var user = await graph.GetUserAsync(request.UserId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {request.UserId} was not found.");

        var portalSettings = await settingsService.GetPortalSettingsAsync(cancellationToken);
        var adSettings = await settingsService.GetAdSettingsAsync(cancellationToken);
        var features = portalSettings.Features;

        var mailboxEnabled = !features.TryGetValue(FeatureFlagKeys.Mailbox, out var mbFlag) || mbFlag;
        var oneDriveEnabled = !features.TryGetValue(FeatureFlagKeys.OneDrive, out var odFlag) || odFlag;
        var hybridEnabled = features.TryGetValue(FeatureFlagKeys.HybridAd, out var hFlag) && hFlag;

        var manager = await graph.GetManagerAsync(user.Id, cancellationToken);
        var groups = await graph.GetUserGroupsAsync(user.Id, cancellationToken);
        var licenses = await graph.GetUserLicensesAsync(user.Id, cancellationToken);
        var ownedGroups = await graph.GetUserOwnedGroupNamesAsync(user.Id, cancellationToken);
        var dirRoles = await graph.GetUserDirectoryRolesAsync(user.Id, cancellationToken);
        MailboxInfo? mailbox = mailboxEnabled ? await exchange.GetMailboxAsync(user.UserPrincipalName, cancellationToken) : null;

        var (warnings, blocking) = OffboardingDomainLogic.EvaluatePreflight(
            mailbox?.SizeGb,
            mailbox?.LitigationHoldEnabled ?? false,
            ownedGroups,
            dirRoles,
            actor.Roles,
            request.ConfirmPrivilegedRoleAsAdmin);

        if (request.Submit && !string.Equals(request.ConfirmationUpn?.Trim(), user.UserPrincipalName, StringComparison.OrdinalIgnoreCase))
        {
            blocking = [.. blocking, $"Type {user.UserPrincipalName} to confirm offboarding."];
        }

        var specs = new List<PlannedStepSpec>();
        var isHybridUser = user.OnPremisesSyncEnabled && hybridEnabled;

        if (isHybridUser)
        {
            var sam = user.OnPremisesSamAccountName ?? OnboardingDomainLogic.DeriveMailNickname(user.UserPrincipalName);
            // Hybrid offboarding order (M9): Graph revoke sessions immediately -> AD disable -> AD password reset -> remove AD groups -> move to Offboarding OU -> sync.delta -> cloud steps
            specs.Add(Spec("graph.user.revokeSessions", new RevokeSessionsStepInput(user.Id)));
            specs.Add(Spec(AdJobKinds.UserSetEnabled, new AdUserSetEnabledPayload(sam, false)));
            specs.Add(Spec(AdJobKinds.UserResetPassword, new AdUserResetPasswordPayload(sam, EphemeralCredentialStore.GenerateStrongPassword(), true)));
            specs.Add(Spec(AdJobKinds.UserRemoveFromAllGroups, new AdUserRemoveFromAllGroupsPayload(sam)));
            var offOu = request.OffboardingOuOverride ?? adSettings.OffboardingOu;
            if (!string.IsNullOrWhiteSpace(offOu))
            {
                specs.Add(Spec(AdJobKinds.UserMove, new AdUserMovePayload(sam, offOu)));
            }

            specs.Add(Spec(AdJobKinds.SyncDelta, new AdSyncDeltaPayload(adSettings.EntraConnectServer)));
        }
        else
        {
            // Cloud offboarding order (M7): 1. Block sign-in, 2. Revoke sessions, 3. Reset password (unshown)
            specs.Add(Spec("graph.user.setAccountEnabled", new SetAccountEnabledStepInput(user.Id, false)));
            specs.Add(Spec("graph.user.revokeSessions", new RevokeSessionsStepInput(user.Id)));
            specs.Add(Spec("graph.user.resetPassword", new ResetCloudPasswordStepInput(user.Id, true, ShowCredentialOnce: false)));
        }

        // 4. Remove MFA methods
        specs.Add(Spec("graph.mfa.requireReRegistration", new RemoveAllMfaStepInput(user.Id, RevokeSessions: false)));

        // 5. Mailbox actions (BEFORE license removal!)
        if (mailboxEnabled)
        {
            if (request.Mailbox.ConvertToShared)
            {
                specs.Add(Spec("exchange.mailbox.convertToShared", new ConvertMailboxToSharedStepInput(user.UserPrincipalName)));
            }

            if (!string.IsNullOrWhiteSpace(request.Mailbox.ForwardToUpn))
            {
                specs.Add(Spec("exchange.mailbox.configureForwarding", new MailboxForwardingStepInput(
                    user.UserPrincipalName,
                    request.Mailbox.ForwardToUpn,
                    request.Mailbox.DeliverToMailboxAndForward)));
            }

            if (!string.IsNullOrWhiteSpace(request.Mailbox.AutoReplyInternal) || !string.IsNullOrWhiteSpace(request.Mailbox.AutoReplyExternal))
            {
                specs.Add(Spec("exchange.mailbox.setAutoReply", new MailboxAutoReplyStepInput(
                    user.UserPrincipalName,
                    request.Mailbox.AutoReplyInternal,
                    request.Mailbox.AutoReplyExternal)));
            }

            if ((request.Mailbox.GrantManagerFullAccess || request.Mailbox.GrantManagerSendAs) && manager is not null)
            {
                specs.Add(Spec("exchange.mailbox.grantAccess", new MailboxGrantAccessStepInput(
                    user.UserPrincipalName,
                    manager.UserPrincipalName,
                    request.Mailbox.GrantManagerFullAccess,
                    request.Mailbox.GrantManagerSendAs)));
            }

            if (request.Mailbox.HideFromAddressLists)
            {
                specs.Add(Spec("exchange.mailbox.setHiddenFromGal", new MailboxHideFromGalStepInput(user.UserPrincipalName, true)));
            }
        }

        // 6. OneDrive handover
        if (oneDriveEnabled && request.OneDrive.GrantManagerAccess && manager is not null)
        {
            specs.Add(Spec("graph.onedrive.grantAccess", new GrantOneDriveAccessStepInput(
                user.Id,
                manager.UserPrincipalName,
                Math.Max(1, request.OneDrive.AccessRetentionDays))));
        }

        // 7. Remove cloud groups & distribution lists (with undo data)
        foreach (var grp in groups.Where(g => !g.OnPremisesSyncEnabled))
        {
            if (grp.GroupType is "Distribution list" or "Mail-enabled security")
            {
                if (mailboxEnabled)
                {
                    specs.Add(Spec("exchange.dl.removeMember", new DistributionListMemberStepInput(grp.Mail ?? grp.Id, grp.DisplayName, user.UserPrincipalName)));
                }
            }
            else
            {
                specs.Add(Spec("graph.group.removeMember", new GroupMemberStepInput(user.Id, grp.Id, grp.DisplayName)));
            }
        }

        // 8. Remove licenses ONLY after mailbox conversion step!
        var keepSet = new HashSet<string>(request.KeepSkuIds ?? [], StringComparer.OrdinalIgnoreCase);
        if (request.RemoveAllLicenses || keepSet.Count > 0)
        {
            foreach (var lic in licenses.Where(l => !l.AssignedViaGroup && !keepSet.Contains(l.SkuId)))
            {
                specs.Add(Spec("graph.license.remove", new RemoveLicenseStepInput(user.Id, lic.SkuId, lic.FriendlyName)));
            }
        }

        // 9. Attributes (clear manager, prefix description)
        if (request.ClearManager && !isHybridUser)
        {
            specs.Add(Spec("graph.user.setManager", new SetManagerStepInput(user.Id, null)));
        }

        if (request.PrefixDescriptionWithLeftDate)
        {
            var leftDate = (request.LeaveDate ?? clock.GetUtcNow()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (isHybridUser)
            {
                var sam = user.OnPremisesSamAccountName ?? OnboardingDomainLogic.DeriveMailNickname(user.UserPrincipalName);
                specs.Add(Spec(AdJobKinds.UserUpdate, new AdUserUpdatePayload(sam, null, null, null, null, null, null, null, null, null, null, $"Left {leftDate}")));
            }
            else
            {
                specs.Add(Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(user.Id, "jobTitle", $"Left {leftDate}")));
            }
        }

        // Enforce strict M7 step ordering invariant before returning or creating run!
        OffboardingDomainLogic.AssertValidStepOrder(specs.Select(s => s.StepKind).ToList());

        if (!request.Submit)
        {
            var planned = await engine.PlanAsync(
                WorkflowRunType.Offboarding,
                specs,
                actor,
                extraWarnings: warnings,
                extraBlockingErrors: blocking,
                cancellationToken: cancellationToken);
            return (planned.Plan, null);
        }

        var run = await engine.CreateRunAsync(
            WorkflowRunType.Offboarding,
            user.UserPrincipalName,
            user.DisplayName,
            specs,
            actor,
            scheduledFor: request.LeaveDate,
            requestPayload: request,
            extraWarnings: warnings,
            extraBlockingErrors: blocking,
            cancellationToken: cancellationToken);

        var dto = engine.ToRunDto(run, actor);
        return (dto.Plan, dto);
    }

    public async Task<(PlanSummaryDto Plan, WorkflowRunDto? Run)> PlanOrSubmitRestoreUserAsync(
        RestoreUserRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var sourceRun = await db.WorkflowRuns.AsNoTracking()
            .Include(r => r.Steps)
            .FirstOrDefaultAsync(r => r.Id == request.OffboardingRunId, cancellationToken)
            ?? throw new KeyNotFoundException($"Offboarding run {request.OffboardingRunId} was not found.");

        var specs = new List<PlannedStepSpec>();
        var warnings = new List<string>
        {
            "The user's mailbox remains a shared mailbox by default. Convert it back to a regular mailbox in Exchange Online after confirming licenses are restored.",
        };

        var undoEntries = sourceRun.Steps
            .Where(s => !string.IsNullOrWhiteSpace(s.UndoDataJson))
            .Select(s => JsonSerializer.Deserialize<UndoDescriptor>(s.UndoDataJson!, JsonOptions))
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();

        if (request.ReEnableSignIn)
        {
            var enableUndo = undoEntries.FirstOrDefault(u => u.StepKind is "graph.user.setAccountEnabled" or AdJobKinds.UserSetEnabled);
            if (enableUndo is not null)
            {
                specs.Add(new PlannedStepSpec(enableUndo.StepKind, enableUndo.InputJson));
            }
            else
            {
                specs.Add(Spec("graph.user.setAccountEnabled", new SetAccountEnabledStepInput(sourceRun.TargetUserRef, true)));
            }
        }

        if (request.RestoreGroups)
        {
            foreach (var grpUndo in undoEntries.Where(u => u.StepKind is "graph.group.addMember" or "exchange.dl.addMember"))
            {
                specs.Add(new PlannedStepSpec(grpUndo.StepKind, grpUndo.InputJson));
            }
        }

        if (request.RestoreLicenses)
        {
            foreach (var licUndo in undoEntries.Where(u => u.StepKind == "graph.license.assign"))
            {
                specs.Add(new PlannedStepSpec(licUndo.StepKind, licUndo.InputJson));
            }
        }

        if (!request.Submit)
        {
            var planned = await engine.PlanAsync(WorkflowRunType.RestoreUser, specs, actor, extraWarnings: warnings, cancellationToken: cancellationToken);
            return (planned.Plan, null);
        }

        var run = await engine.CreateRunAsync(
            WorkflowRunType.RestoreUser,
            sourceRun.TargetUserRef,
            sourceRun.TargetUserDisplayName,
            specs,
            actor,
            requestPayload: request,
            extraWarnings: warnings,
            cancellationToken: cancellationToken);
        var dto = engine.ToRunDto(run, actor);
        return (dto.Plan, dto);
    }

    public async Task<(PlanSummaryDto Plan, WorkflowRunDto? Run)> PlanOrSubmitAttributeUpdateAsync(
        string userId,
        UpdateUserAttributesRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var user = await graph.GetUserAsync(userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");
        var portalSettings = await settingsService.GetPortalSettingsAsync(cancellationToken);
        var adSettings = await settingsService.GetAdSettingsAsync(cancellationToken);
        var hybridEnabled = portalSettings.Features.TryGetValue(FeatureFlagKeys.HybridAd, out var hf) && hf;

        var specs = new List<PlannedStepSpec>();
        var blocking = new List<string>();
        var adUpdatesNeeded = false;

        void AddAttr(string attrName, string? val)
        {
            if (val is null)
            {
                return;
            }

            if (user.OnPremisesSyncEnabled && HybridRoutingPolicy.IsAdMasteredAttribute(attrName))
            {
                if (!hybridEnabled)
                {
                    blocking.Add(HybridRoutingPolicy.ValidateGraphAttributeEdit(true, attrName, false)!);
                }
                else
                {
                    adUpdatesNeeded = true;
                }
            }
            else
            {
                specs.Add(Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(user.Id, attrName, val)));
            }
        }

        AddAttr("jobTitle", request.JobTitle);
        AddAttr("department", request.Department);
        AddAttr("officeLocation", request.OfficeLocation);
        AddAttr("businessPhone", request.BusinessPhone);
        AddAttr("mobilePhone", request.MobilePhone);
        AddAttr("employeeId", request.EmployeeId);
        AddAttr("usageLocation", request.UsageLocation);
        AddAttr("companyName", request.CompanyName);

        if (request.CustomAttributes is not null)
        {
            foreach (var kv in request.CustomAttributes)
            {
                specs.Add(Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(user.Id, kv.Key, kv.Value)));
            }
        }

        if (request.ManagerId is not null)
        {
            if (user.OnPremisesSyncEnabled && !hybridEnabled)
            {
                blocking.Add(HybridRoutingPolicy.ValidateGraphAttributeEdit(true, "manager", false)!);
            }
            else if (user.OnPremisesSyncEnabled)
            {
                adUpdatesNeeded = true;
            }
            else
            {
                specs.Add(Spec("graph.user.setManager", new SetManagerStepInput(user.Id, string.IsNullOrWhiteSpace(request.ManagerId) ? null : request.ManagerId)));
            }
        }

        if (adUpdatesNeeded)
        {
            var sam = user.OnPremisesSamAccountName ?? OnboardingDomainLogic.DeriveMailNickname(user.UserPrincipalName);
            specs.Add(Spec(AdJobKinds.UserUpdate, new AdUserUpdatePayload(
                sam,
                null,
                null,
                request.Department,
                request.JobTitle,
                request.OfficeLocation,
                request.BusinessPhone,
                request.MobilePhone,
                request.CompanyName,
                request.EmployeeId,
                request.ManagerId,
                null)));
            specs.Add(Spec(AdJobKinds.SyncDelta, new AdSyncDeltaPayload(adSettings.EntraConnectServer)));
        }

        if (!request.Submit)
        {
            var planned = await engine.PlanAsync(WorkflowRunType.AttributeUpdate, specs, actor, extraBlockingErrors: blocking, cancellationToken: cancellationToken);
            return (planned.Plan, null);
        }

        var run = await engine.CreateRunAsync(
            WorkflowRunType.AttributeUpdate,
            user.UserPrincipalName,
            user.DisplayName,
            specs,
            actor,
            requestPayload: request,
            extraBlockingErrors: blocking,
            cancellationToken: cancellationToken);
        var dto = engine.ToRunDto(run, actor);
        return (dto.Plan, dto);
    }

    public async Task<(PlanSummaryDto Plan, WorkflowRunDto? Run)> PlanOrSubmitLicenseChangeAsync(
        LicenseChangeRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var portalSettings = await settingsService.GetPortalSettingsAsync(cancellationToken);
        var specs = new List<PlannedStepSpec>();
        string targetRef = request.UserIds.Count == 1 ? request.UserIds[0] : $"{request.UserIds.Count} users";
        string? targetName = null;

        foreach (var uid in request.UserIds)
        {
            var user = await graph.GetUserAsync(uid, cancellationToken)
                ?? throw new KeyNotFoundException($"User {uid} was not found.");
            if (request.UserIds.Count == 1)
            {
                targetRef = user.UserPrincipalName;
                targetName = user.DisplayName;
            }

            // M5 criterion 4: automatically add set usage location step when assigning a license to a user without usageLocation.
            if (request.AddSkuIds.Count > 0 && string.IsNullOrWhiteSpace(user.UsageLocation))
            {
                specs.Add(Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(
                    user.Id,
                    "usageLocation",
                    portalSettings.DefaultUsageLocation)));
            }

            foreach (var addSku in request.AddSkuIds)
            {
                var disabled = request.DisabledPlansBySku is not null && request.DisabledPlansBySku.TryGetValue(addSku, out var dp)
                    ? dp
                    : [];
                specs.Add(Spec("graph.license.assign", new AssignLicenseStepInput(user.Id, addSku, disabled)));
            }

            foreach (var removeSku in request.RemoveSkuIds)
            {
                specs.Add(Spec("graph.license.remove", new RemoveLicenseStepInput(user.Id, removeSku, null)));
            }
        }

        if (!request.Submit)
        {
            var planned = await engine.PlanAsync(WorkflowRunType.LicenseAssignment, specs, actor, cancellationToken: cancellationToken);
            return (planned.Plan, null);
        }

        var run = await engine.CreateRunAsync(
            WorkflowRunType.LicenseAssignment,
            targetRef,
            targetName ?? targetRef,
            specs,
            actor,
            requestPayload: request,
            cancellationToken: cancellationToken);
        var dto = engine.ToRunDto(run, actor);
        return (dto.Plan, dto);
    }

    public async Task<(PlanSummaryDto Plan, WorkflowRunDto? Run)> PlanOrSubmitGroupMembershipAsync(
        GroupMembershipChangeRequest request,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var remove = string.Equals(request.Action, "remove", StringComparison.OrdinalIgnoreCase);
        var specs = new List<PlannedStepSpec>();

        foreach (var groupId in request.GroupIds)
        {
            var detail = await graph.GetGroupDetailAsync(groupId, cancellationToken);
            var group = detail?.Group;
            var groupName = group?.DisplayName ?? groupId;
            var isDl = group?.GroupType is "Distribution list" or "Mail-enabled security";
            var isAd = group?.OnPremisesSyncEnabled ?? false;

            if (!isDl && !isAd && request.UserIds.Count >= 5)
            {
                // M5 criterion 2: use Graph $batch for bulk group updates (<= 3 HTTP requests for 50 users).
                specs.Add(Spec("graph.group.batchMembers", new BatchGroupMembersStepInput(groupId, groupName, request.UserIds, remove)));
                continue;
            }

            foreach (var uid in request.UserIds)
            {
                var user = await graph.GetUserAsync(uid, cancellationToken);
                var upn = user?.UserPrincipalName ?? uid;
                if (isDl)
                {
                    specs.Add(Spec(
                        remove ? "exchange.dl.removeMember" : "exchange.dl.addMember",
                        new DistributionListMemberStepInput(group?.Mail ?? groupId, groupName, upn)));
                }
                else if (isAd)
                {
                    var sam = user?.OnPremisesSamAccountName ?? upn;
                    specs.Add(Spec(
                        remove ? AdJobKinds.GroupRemoveMember : AdJobKinds.GroupAddMember,
                        new AdGroupMembershipPayload(group?.OnPremisesDistinguishedName ?? groupName, sam)));
                }
                else
                {
                    specs.Add(Spec(
                        remove ? "graph.group.removeMember" : "graph.group.addMember",
                        new GroupMemberStepInput(uid, groupId, groupName)));
                }
            }
        }

        var targetRef = request.UserIds.Count == 1 ? request.UserIds[0] : $"{request.UserIds.Count} users / {request.GroupIds.Count} group(s)";
        if (!request.Submit)
        {
            var planned = await engine.PlanAsync(WorkflowRunType.GroupMembership, specs, actor, cancellationToken: cancellationToken);
            return (planned.Plan, null);
        }

        var run = await engine.CreateRunAsync(
            WorkflowRunType.GroupMembership,
            targetRef,
            targetRef,
            specs,
            actor,
            requestPayload: request,
            cancellationToken: cancellationToken);
        var dto = engine.ToRunDto(run, actor);
        return (dto.Plan, dto);
    }

    public static PlannedStepSpec Spec<T>(string kind, T input) =>
        new(kind, JsonSerializer.Serialize(input, JsonOptions));
}
