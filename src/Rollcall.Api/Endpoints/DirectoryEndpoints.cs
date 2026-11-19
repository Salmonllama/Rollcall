// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Api.Auth;
using Rollcall.Contracts;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;
using Rollcall.Infrastructure.Workflow.Steps;

namespace Rollcall.Api.Endpoints;

public static class DirectoryEndpoints
{
    public static void MapDirectoryEndpoints(this WebApplication app)
    {
        // --- Dashboard (M4) -----------------------------------------------------------------

        app.MapGet("/api/dashboard", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            IGraphClient graph,
            DirectoryCacheService cache,
            AppCertificateService certService,
            TimeProvider clock,
            bool? refresh,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var now = clock.GetUtcNow();
            var runs = await db.WorkflowRuns.AsNoTracking()
                .Include(r => r.Steps)
                .OrderByDescending(r => r.CreatedAt)
                .Take(200)
                .ToListAsync(ct);

            var upcomingOnboardings = runs
                .Where(r => r.Type == WorkflowRunType.Onboarding && r.Status == WorkflowRunStatus.Scheduled && r.ScheduledFor >= now.AddHours(-1))
                .OrderBy(r => r.ScheduledFor)
                .Select(WorkflowEngine.ToListItemDto)
                .ToList();
            var upcomingOffboardings = runs
                .Where(r => r.Type == WorkflowRunType.Offboarding && r.Status == WorkflowRunStatus.Scheduled && r.ScheduledFor >= now.AddHours(-1))
                .OrderBy(r => r.ScheduledFor)
                .Select(WorkflowEngine.ToListItemDto)
                .ToList();
            var pendingApprovals = runs
                .Where(r => r.Status == WorkflowRunStatus.AwaitingApproval)
                .Select(WorkflowEngine.ToListItemDto)
                .ToList();
            var recentFailures = runs
                .Where(r => r.Status is WorkflowRunStatus.PartiallyFailed or WorkflowRunStatus.NeedsReview)
                .Take(10)
                .Select(WorkflowEngine.ToListItemDto)
                .ToList();

            var force = refresh == true;
            var skus = await SafeFetchAsync(() => cache.GetOrFetchAsync("skus:all", "skus", force, graph.ListSubscribedSkusAsync, ct), []);
            var disabledUsers = await SafeFetchAsync(
                () => cache.GetOrFetchAsync("users:disabled", "users", force, c => graph.SearchUsersAsync(null, null, false, null, true, 500, c), ct),
                []);

            var activeCert = await db.AppCertificates.AsNoTracking()
                .Where(c => c.Status == AppCertificateStatus.Active)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var certWarn = actor!.Roles.Contains(PortalRole.Admin) && certService.ShouldWarnExpiry(activeCert);
            var certDays = activeCert is not null ? certService.ToSummary(activeCert).DaysUntilExpiry : (int?)null;

            var agentCutoff = now - AgentJobDispatcher.AgentHeartbeatMaxAge;
            var agents = await db.AdAgents.AsNoTracking().Where(a => a.Status != AgentStatus.Revoked).ToListAsync(ct);
            var connectedAgents = agents.Count(a => a.Status == AgentStatus.Connected && a.LastSeenAt >= agentCutoff);
            var offlineAgents = agents.Count - connectedAgents;

            return Results.Ok(new DashboardSummaryDto(
                UpcomingOnboardingsCount: upcomingOnboardings.Count,
                UpcomingOffboardingsCount: upcomingOffboardings.Count,
                PendingApprovalsCount: pendingApprovals.Count,
                RecentFailuresCount: recentFailures.Count,
                AvailableLicenseSeats: skus.Sum(s => s.AvailableSeats),
                LicensedDisabledAccountsCount: disabledUsers.Count(u => !u.AccountEnabled && u.LicenseCount > 0),
                ShowCertExpiryWarning: certWarn,
                CertDaysUntilExpiry: certDays,
                ConnectedAgentsCount: connectedAgents,
                OfflineAgentsCount: offlineAgents,
                UpcomingOnboardings: upcomingOnboardings.Take(10).ToList(),
                UpcomingOffboardings: upcomingOffboardings.Take(10).ToList(),
                PendingApprovals: pendingApprovals.Take(10).ToList(),
                RecentFailures: recentFailures));
        });

        // --- Users (M4, M5) -----------------------------------------------------------------

        app.MapGet("/api/users", async (
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            DirectoryCacheService cache,
            string? q,
            string? department,
            bool? enabled,
            bool? synced,
            bool? licensed,
            int? limit,
            bool? refresh,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var take = Math.Clamp(limit ?? 50, 1, 500);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var matched = await graph.SearchUsersAsync(q, department, enabled, synced, licensed, take, ct);
                return Results.Ok(matched);
            }

            var cacheKey = $"users:{department}:{enabled}:{synced}:{licensed}:{take}";
            var users = await cache.GetOrFetchAsync(
                cacheKey,
                "users",
                refresh == true,
                c => graph.SearchUsersAsync(null, department, enabled, synced, licensed, take, c),
                ct);
            return Results.Ok(users);
        });

        app.MapGet("/api/users/{userId}", async (
            string userId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            IGraphClient graph,
            SettingsService settingsService,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var user = await graph.GetUserAsync(userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            var settings = await settingsService.GetPortalSettingsAsync(ct);
            var manager = await graph.GetManagerAsync(user.Id, ct);
            var groups = await graph.GetUserGroupsAsync(user.Id, ct);
            var licenses = await graph.GetUserLicensesAsync(user.Id, ct);
            var mfa = settings.Features.GetValueOrDefault(FeatureFlagKeys.Mfa, true)
                ? await SafeFetchAsync(() => graph.GetUserMfaMethodsAsync(user.Id, ct), [])
                : [];
            var dirRoles = await SafeFetchAsync(() => graph.GetUserDirectoryRolesAsync(user.Id, ct), []);
            var customAttrs = await SafeFetchAsync(() => graph.GetUserCustomAttributesAsync(user.Id, ct), new Dictionary<string, string>());

            var groupIdSet = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var roleIdSet = dirRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);

            var caPolicies = settings.Features.GetValueOrDefault(FeatureFlagKeys.ConditionalAccess, true)
                ? await SafeFetchAsync(() => graph.ListConditionalAccessPoliciesAsync(ct), [])
                : [];
            var activeTempReversals = await db.ScheduledReversals.AsNoTracking()
                .Where(r => !r.Executed && r.Kind == "ca.removeTemporaryExclusion" && r.TargetRef == user.Id)
                .ToListAsync(ct);

            var caApplicability = caPolicies.Select(p =>
            {
                var enriched = p with
                {
                    ActiveTemporaryExclusions = activeTempReversals
                        .Where(r => r.ActionPayloadJson.Contains(p.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(r => new ScheduledReversalDto(r.Id, r.RunId, r.Kind, r.TargetRef, r.Description, r.DueAt, r.Executed, r.ExecutedAt, r.Outcome))
                        .ToList(),
                };
                return CaApplicabilityEvaluator.Evaluate(enriched, user.Id, groupIdSet, roleIdSet);
            }).ToList();

            var recentRuns = await db.WorkflowRuns.AsNoTracking()
                .Include(r => r.Steps)
                .Where(r => r.TargetUserRef == user.UserPrincipalName || r.TargetUserRef == user.Id)
                .OrderByDescending(r => r.CreatedAt)
                .Take(20)
                .ToListAsync(ct);

            var auditQuery = db.AuditEvents.AsNoTracking()
                .Where(a => a.Target == user.UserPrincipalName || a.Target == user.Id);
            if (!RoleAuthorizationPolicy.CanViewAllAuditEvents(actor!.Roles))
            {
                auditQuery = auditQuery.Where(a => EF.Functions.ILike(a.ActorUpn, actor.Upn));
            }

            var recentAudit = await auditQuery
                .OrderByDescending(a => a.Id)
                .Take(25)
                .Select(a => AuditService.ToDto(a))
                .ToListAsync(ct);

            return Results.Ok(new DirectoryUserDetailDto(
                user,
                manager,
                groups,
                licenses,
                mfa,
                caApplicability,
                dirRoles,
                customAttrs,
                recentRuns.Select(WorkflowEngine.ToListItemDto).ToList(),
                recentAudit));
        });

        app.MapPost("/api/users/{userId}/attributes", async (
            string userId,
            UpdateUserAttributesRequest request,
            HttpContext ctx,
            PortalSessionService session,
            WorkflowPlanners planners,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var (plan, run) = await planners.PlanOrSubmitAttributeUpdateAsync(userId, request, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        app.MapPost("/api/users/bulk-attributes", async (
            BulkUpdateAttributesRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var specs = new List<PlannedStepSpec>();
            foreach (var uid in request.UserIds)
            {
                if (request.JobTitle is not null)
                {
                    specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, "jobTitle", request.JobTitle)));
                }

                if (request.Department is not null)
                {
                    specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, "department", request.Department)));
                }

                if (request.OfficeLocation is not null)
                {
                    specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, "officeLocation", request.OfficeLocation)));
                }

                if (request.CompanyName is not null)
                {
                    specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, "companyName", request.CompanyName)));
                }

                if (request.UsageLocation is not null)
                {
                    specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, "usageLocation", request.UsageLocation)));
                }

                if (request.CustomAttributes is not null)
                {
                    foreach (var kv in request.CustomAttributes)
                    {
                        specs.Add(WorkflowPlanners.Spec("graph.user.updateAttributes", new UpdateUserAttributesStepInput(uid, kv.Key, kv.Value)));
                    }
                }
            }

            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.BulkAttributeUpdate, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var firstUser = request.UserIds.Count == 1 ? await graph.GetUserAsync(request.UserIds[0], ct) : null;
            var targetRef = firstUser?.UserPrincipalName ?? $"{request.UserIds.Count} users";
            var run = await engine.CreateRunAsync(WorkflowRunType.BulkAttributeUpdate, targetRef, targetRef, specs, actor!, requestPayload: request, cancellationToken: ct);
            return Results.Ok(new { plan = engine.ToRunDto(run, actor).Plan, run = engine.ToRunDto(run, actor) });
        });

        app.MapPost("/api/users/{userId}/mfa/remove", async (
            string userId,
            RemoveMfaMethodRequest request,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Mfa, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "MFA management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var specs = new[] { WorkflowPlanners.Spec("graph.mfa.removeMethod", new RemoveMfaMethodStepInput(userId, request.MethodId, request.MethodType)) };
            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.MfaAction, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(WorkflowRunType.MfaAction, userId, userId, specs, actor!, requestPayload: request, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        app.MapPost("/api/users/{userId}/mfa/require-reregistration", async (
            string userId,
            bool? submit,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Mfa, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "MFA management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var specs = new[] { WorkflowPlanners.Spec("graph.mfa.requireReRegistration", new RemoveAllMfaStepInput(userId, RevokeSessions: true)) };
            if (submit != true)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.MfaAction, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(WorkflowRunType.MfaAction, userId, userId, specs, actor!, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        app.MapPost("/api/users/{userId}/mfa/tap", async (
            string userId,
            IssueTapRequest request,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Mfa, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "MFA management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var specs = new[] { WorkflowPlanners.Spec("graph.mfa.issueTap", new IssueTapStepInput(userId, Math.Clamp(request.LifetimeInMinutes, 10, 43200), request.IsUsableOnce)) };
            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.MfaAction, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(WorkflowRunType.MfaAction, userId, userId, specs, actor!, requestPayload: request, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        app.MapPost("/api/users/{userId}/revoke-sessions", async (
            string userId,
            bool? submit,
            HttpContext ctx,
            PortalSessionService session,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var specs = new[] { WorkflowPlanners.Spec("graph.user.revokeSessions", new RevokeSessionsStepInput(userId)) };
            if (submit != true)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.SessionRevoke, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(WorkflowRunType.SessionRevoke, userId, userId, specs, actor!, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        app.MapPost("/api/users/{userId}/reset-password", async (
            string userId,
            ResetPasswordRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var user = await graph.GetUserAsync(userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var hybridEnabled = portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.HybridAd, false);

            PlannedStepSpec[] specs;
            if (user.OnPremisesSyncEnabled)
            {
                if (!hybridEnabled)
                {
                    return Results.Problem(
                        title: "Synced user password reset requires Hybrid AD",
                        detail: "This user is synced from on-prem Active Directory. Enable the Hybrid AD agent in Settings to reset their password.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var sam = user.OnPremisesSamAccountName ?? OnboardingDomainLogic.DeriveMailNickname(user.UserPrincipalName);
                specs = [WorkflowPlanners.Spec(AdJobKinds.UserResetPassword, new AdUserResetPasswordPayload(sam, EphemeralCredentialStore.GenerateStrongPassword(), request.ForceChangeNextSignIn))];
            }
            else
            {
                specs = [WorkflowPlanners.Spec("graph.user.resetPassword", new ResetCloudPasswordStepInput(user.Id, request.ForceChangeNextSignIn, ShowCredentialOnce: true))];
            }

            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.PasswordReset, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(WorkflowRunType.PasswordReset, user.UserPrincipalName, user.DisplayName, specs, actor!, requestPayload: request, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        // --- Groups (M4, M5, M9) ------------------------------------------------------------

        app.MapGet("/api/groups", async (
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            DirectoryCacheService cache,
            string? q,
            bool? refresh,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                return Results.Ok(await graph.ListGroupsAsync(q, ct));
            }

            var groups = await cache.GetOrFetchAsync("groups:all", "groups", refresh == true, c => graph.ListGroupsAsync(null, c), ct);
            return Results.Ok(groups);
        });

        app.MapGet("/api/groups/{groupId}", async (
            string groupId,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var detail = await graph.GetGroupDetailAsync(groupId, ct);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        app.MapPost("/api/groups/membership", async (
            GroupMembershipChangeRequest request,
            HttpContext ctx,
            PortalSessionService session,
            WorkflowPlanners planners,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var (plan, run) = await planners.PlanOrSubmitGroupMembershipAsync(request, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        app.MapPost("/api/groups/copy-from-user", async (
            CopyGroupsRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            WorkflowPlanners planners,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var sourceGroups = await graph.GetUserGroupsAsync(request.SourceUserId, ct);
            var req = new GroupMembershipChangeRequest(
                [request.TargetUserId],
                sourceGroups.Select(g => g.Id).ToList(),
                "add",
                request.Submit);
            var (plan, run) = await planners.PlanOrSubmitGroupMembershipAsync(req, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        app.MapPost("/api/groups/ad", async (
            AdGroupManagementRequest request,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.HybridAd, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "Hybrid AD management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var spec = request.Operation.ToLowerInvariant() switch
            {
                "create" => WorkflowPlanners.Spec(AdJobKinds.GroupCreate, new AdGroupCreatePayload(
                    request.Name ?? string.Empty,
                    request.SamAccountName ?? request.Name ?? string.Empty,
                    request.TargetOu ?? string.Empty,
                    request.Scope ?? "Global",
                    request.Category ?? "Security",
                    request.Description)),
                "update" => WorkflowPlanners.Spec(AdJobKinds.GroupUpdate, new AdGroupUpdatePayload(
                    request.GroupDnOrSam ?? string.Empty,
                    request.Name,
                    request.Description,
                    request.Scope,
                    request.Category)),
                "move" => WorkflowPlanners.Spec(AdJobKinds.GroupMove, new AdGroupMovePayload(
                    request.GroupDnOrSam ?? string.Empty,
                    request.TargetOu ?? string.Empty)),
                "addmember" => WorkflowPlanners.Spec(AdJobKinds.GroupAddMember, new AdGroupMembershipPayload(
                    request.GroupDnOrSam ?? string.Empty,
                    request.MemberDnOrSam ?? string.Empty)),
                "removemember" => WorkflowPlanners.Spec(AdJobKinds.GroupRemoveMember, new AdGroupMembershipPayload(
                    request.GroupDnOrSam ?? string.Empty,
                    request.MemberDnOrSam ?? string.Empty)),
                _ => throw new ArgumentException($"Unsupported AD group operation \"{request.Operation}\"."),
            };

            var specs = new[] { spec };
            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.AdGroupManagement, specs, actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var target = request.GroupDnOrSam ?? request.Name ?? "AD group";
            var run = await engine.CreateRunAsync(WorkflowRunType.AdGroupManagement, target, target, specs, actor!, requestPayload: request, cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        // --- Licenses (M4, M5) --------------------------------------------------------------

        app.MapGet("/api/licenses", async (
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            DirectoryCacheService cache,
            SettingsService settings,
            bool? refresh,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Licenses, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "License management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var force = refresh == true;
            var skus = await cache.GetOrFetchAsync("skus:all", "skus", force, graph.ListSubscribedSkusAsync, ct);
            var disabled = await cache.GetOrFetchAsync(
                "users:disabled-licensed",
                "users",
                force,
                c => graph.SearchUsersAsync(null, null, false, null, true, 500, c),
                ct);

            var licensedDisabled = new List<LicensedDisabledUserDto>();
            foreach (var u in disabled.Where(x => !x.AccountEnabled && x.LicenseCount > 0))
            {
                var lics = await graph.GetUserLicensesAsync(u.Id, ct);
                licensedDisabled.Add(new LicensedDisabledUserDto(u.Id, u.UserPrincipalName, u.DisplayName, lics));
            }

            return Results.Ok(new LicensesOverviewDto(skus, licensedDisabled));
        });

        app.MapPost("/api/licenses/change", async (
            LicenseChangeRequest request,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            WorkflowPlanners planners,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Licenses, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "License management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var (plan, run) = await planners.PlanOrSubmitLicenseChangeAsync(request, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        // --- Conditional Access (M8: Admin-only edits!) -------------------------------------

        app.MapGet("/api/conditional-access", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            IGraphClient graph,
            SettingsService settings,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.ConditionalAccess, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "Conditional Access management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var policies = await graph.ListConditionalAccessPoliciesAsync(ct);
            var activeReversals = await db.ScheduledReversals.AsNoTracking()
                .Where(r => !r.Executed && r.Kind == "ca.removeTemporaryExclusion")
                .ToListAsync(ct);

            var enriched = policies.Select(p => p with
            {
                ActiveTemporaryExclusions = activeReversals
                    .Where(r => r.ActionPayloadJson.Contains(p.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(r => new ScheduledReversalDto(r.Id, r.RunId, r.Kind, r.TargetRef, r.Description, r.DueAt, r.Executed, r.ExecutedAt, r.Outcome))
                    .ToList(),
            }).ToList();

            return Results.Ok(enriched);
        });

        app.MapPost("/api/conditional-access/{policyId}/membership", async (
            string policyId,
            EditCaPolicyMembershipRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            // M8 criterion 1: Only Admins can change CA membership; Technicians and Approvers get 403.
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.ConditionalAccess, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "Conditional Access management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var policy = await graph.GetConditionalAccessPolicyAsync(policyId, ct);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var etagHeader = ctx.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"') ?? request.ExpectedETag;
            var spec = WorkflowPlanners.Spec("graph.ca.updateMembership", new UpdateCaMembershipStepInput(
                policyId,
                request.IncludeUsers,
                request.ExcludeUsers,
                request.IncludeGroups,
                request.ExcludeGroups,
                etagHeader,
                TemporaryExclusionExpiresAt: null,
                TemporaryExcludedUserId: null));

            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.ConditionalAccessEdit, [spec], actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(
                WorkflowRunType.ConditionalAccessEdit,
                policy.Id,
                policy.DisplayName,
                [spec],
                actor!,
                requestPayload: request,
                cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });

        app.MapPost("/api/conditional-access/temporary-exclusion", async (
            AddTemporaryCaExclusionRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            SettingsService settings,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            if (!portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.ConditionalAccess, true))
            {
                return Results.Problem(title: "Feature disabled", detail: "Conditional Access management is turned off in Settings.", statusCode: StatusCodes.Status403Forbidden);
            }

            var policy = await graph.GetConditionalAccessPolicyAsync(request.PolicyId, ct);
            if (policy is null)
            {
                return Results.NotFound();
            }

            var nextExcludes = policy.ExcludeUsers.Append(request.UserId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var spec = WorkflowPlanners.Spec("graph.ca.updateMembership", new UpdateCaMembershipStepInput(
                policy.Id,
                policy.IncludeUsers,
                nextExcludes,
                policy.IncludeGroups,
                policy.ExcludeGroups,
                policy.ETag,
                request.ExpiresAt,
                request.UserId));

            if (!request.Submit)
            {
                var planned = await engine.PlanAsync(WorkflowRunType.ConditionalAccessEdit, [spec], actor!, cancellationToken: ct);
                return Results.Ok(new { plan = planned.Plan, run = (WorkflowRunDto?)null });
            }

            var run = await engine.CreateRunAsync(
                WorkflowRunType.ConditionalAccessEdit,
                policy.Id,
                policy.DisplayName,
                [spec],
                actor!,
                requestPayload: request,
                cancellationToken: ct);
            var dto = engine.ToRunDto(run, actor);
            return Results.Ok(new { plan = dto.Plan, run = dto });
        });
    }

    private static async Task<T> SafeFetchAsync<T>(Func<Task<T>> call, T fallback)
    {
        try
        {
            return await call();
        }
        catch (StepExecutionException)
        {
            return fallback;
        }
    }
}
