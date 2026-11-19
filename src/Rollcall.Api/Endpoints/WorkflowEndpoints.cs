// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Api.Auth;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Infrastructure.Documents;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Api.Endpoints;

public static class WorkflowEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapWorkflowEndpoints(this WebApplication app)
    {
        // --- Role Templates (M6: Admin-only write, live validation, versioned, ETag) --------

        app.MapGet("/api/templates", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            bool? includeArchived,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var query = db.RoleTemplates.AsNoTracking().AsQueryable();
            if (includeArchived != true)
            {
                query = query.Where(t => !t.IsArchived);
            }

            var rows = await query.OrderBy(t => t.Name).ToListAsync(ct);
            return Results.Ok(rows.Select(ToTemplateDto).ToList());
        });

        app.MapPost("/api/templates/validate", async (
            SaveRoleTemplateRequest request,
            HttpContext ctx,
            PortalSessionService session,
            IGraphClient graph,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var validation = await ValidateTemplateAgainstTenantAsync(request, graph, ct);
            return Results.Ok(validation);
        });

        app.MapPost("/api/templates", async (
            SaveRoleTemplateRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            IGraphClient graph,
            AuditService audit,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var validation = await ValidateTemplateAgainstTenantAsync(request, graph, ct);
            if (!validation.IsValid)
            {
                return Results.Problem(
                    title: "Role template validation failed",
                    detail: string.Join(" ", validation.Errors),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var now = clock.GetUtcNow();
            var defJson = JsonSerializer.Serialize(request.Definition, JsonOptions);
            var entity = new RoleTemplateEntity
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Description = request.Description?.Trim(),
                Version = 1,
                DefinitionJson = defJson,
                IsArchived = false,
                ETag = ComputeETag(defJson + "|1"),
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedByUpn = actor!.Upn,
            };
            db.RoleTemplates.Add(entity);
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor.Upn, actor.DisplayName, "template.created", entity.Name, "succeeded",
                new { entity.Id, entity.Version }, summary: $"Created role template \"{entity.Name}\" (v1).", cancellationToken: ct);

            ctx.Response.Headers.ETag = $"\"{entity.ETag}\"";
            return Results.Created($"/api/templates/{entity.Id}", ToTemplateDto(entity));
        });

        app.MapPut("/api/templates/{id:guid}", async (
            Guid id,
            SaveRoleTemplateRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            IGraphClient graph,
            AuditService audit,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var entity = await db.RoleTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            var ifMatch = ctx.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
            if (!string.IsNullOrWhiteSpace(ifMatch) && !string.Equals(ifMatch, entity.ETag, StringComparison.Ordinal))
            {
                return Results.Problem(
                    title: "Template modified",
                    detail: "This role template was updated by someone else. Reload it before saving.",
                    statusCode: StatusCodes.Status412PreconditionFailed);
            }

            var validation = await ValidateTemplateAgainstTenantAsync(request, graph, ct);
            if (!validation.IsValid)
            {
                return Results.Problem(
                    title: "Role template validation failed",
                    detail: string.Join(" ", validation.Errors),
                    statusCode: StatusCodes.Status400BadRequest);
            }

            entity.Name = request.Name.Trim();
            entity.Description = request.Description?.Trim();
            entity.Version++;
            entity.DefinitionJson = JsonSerializer.Serialize(request.Definition, JsonOptions);
            entity.ETag = ComputeETag(entity.DefinitionJson + "|" + entity.Version);
            entity.UpdatedAt = clock.GetUtcNow();
            entity.UpdatedByUpn = actor!.Upn;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor.Upn, actor.DisplayName, "template.updated", entity.Name, "succeeded",
                new { entity.Id, entity.Version }, summary: $"Updated role template \"{entity.Name}\" to v{entity.Version}.", cancellationToken: ct);

            ctx.Response.Headers.ETag = $"\"{entity.ETag}\"";
            return Results.Ok(ToTemplateDto(entity));
        });

        app.MapPost("/api/templates/{id:guid}/archive", async (
            Guid id,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AuditService audit,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var entity = await db.RoleTemplates.FirstOrDefaultAsync(t => t.Id == id, ct);
            if (entity is null)
            {
                return Results.NotFound();
            }

            entity.IsArchived = !entity.IsArchived;
            entity.UpdatedAt = clock.GetUtcNow();
            entity.UpdatedByUpn = actor!.Upn;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor.Upn, actor.DisplayName, entity.IsArchived ? "template.archived" : "template.restored",
                entity.Name, "succeeded", new { entity.Id }, cancellationToken: ct);
            return Results.Ok(ToTemplateDto(entity));
        });

        app.MapPost("/api/templates/{id:guid}/duplicate", async (
            Guid id,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var src = await db.RoleTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (src is null)
            {
                return Results.NotFound();
            }

            var now = clock.GetUtcNow();
            var copy = new RoleTemplateEntity
            {
                Id = Guid.NewGuid(),
                Name = $"{src.Name} (copy)",
                Description = src.Description,
                Version = 1,
                DefinitionJson = src.DefinitionJson,
                IsArchived = false,
                ETag = ComputeETag(src.DefinitionJson + "|copy|" + now.Ticks),
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedByUpn = actor!.Upn,
            };
            db.RoleTemplates.Add(copy);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/templates/{copy.Id}", ToTemplateDto(copy));
        });

        // --- Onboarding & Welcome Pack (M6) -------------------------------------------------

        app.MapPost("/api/onboarding", async (
            OnboardingRequest request,
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

            var response = await planners.PlanOrSubmitOnboardingAsync(request, actor!, ct);
            return Results.Ok(response);
        });

        app.MapGet("/api/runs/{runId:guid}/welcome-pack", async (
            Guid runId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            SettingsService settings,
            EphemeralCredentialStore credentials,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var pack = await BuildWelcomePackAsync(runId, db, settings, credentials, consumeCredential: true, ct);
            return pack is null ? Results.NotFound() : Results.Ok(pack);
        });

        app.MapGet("/api/runs/{runId:guid}/welcome-pack.pdf", async (
            Guid runId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            SettingsService settings,
            EphemeralCredentialStore credentials,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var pack = await BuildWelcomePackAsync(runId, db, settings, credentials, consumeCredential: true, ct);
            if (pack is null)
            {
                return Results.NotFound();
            }

            var headerBytes = await settings.GetWelcomePackAssetBytesAsync("header", ct);
            var letterheadBytes = await settings.GetWelcomePackAssetBytesAsync("letterhead", ct);
            var pdf = WelcomePackPdfGenerator.Generate(pack, headerBytes, letterheadBytes);
            return Results.File(pdf, "application/pdf", $"welcome-pack-{runId:N}.pdf");
        });

        // --- Offboarding & Restore (M7) -----------------------------------------------------

        app.MapPost("/api/offboarding", async (
            OffboardingRequest request,
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

            var (plan, run) = await planners.PlanOrSubmitOffboardingAsync(request, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        app.MapPost("/api/offboarding/restore", async (
            RestoreUserRequest request,
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

            var (plan, run) = await planners.PlanOrSubmitRestoreUserAsync(request, actor!, ct);
            return Results.Ok(new { plan, run });
        });

        // --- Runs & One-time Credential View (M3, M5) ---------------------------------------

        app.MapGet("/api/runs", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            WorkflowRunStatus? status,
            WorkflowRunType? type,
            int? limit,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var query = db.WorkflowRuns.AsNoTracking().Include(r => r.Steps).AsQueryable();
            if (status.HasValue)
            {
                query = query.Where(r => r.Status == status.Value);
            }

            if (type.HasValue)
            {
                query = query.Where(r => r.Type == type.Value);
            }

            var rows = await query
                .OrderByDescending(r => r.CreatedAt)
                .Take(Math.Clamp(limit ?? 100, 1, 500))
                .ToListAsync(ct);

            return Results.Ok(rows.Select(WorkflowEngine.ToListItemDto).ToList());
        });

        app.MapGet("/api/runs/{runId:guid}", async (
            Guid runId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var run = await db.WorkflowRuns.AsNoTracking()
                .Include(r => r.Steps)
                .Include(r => r.Approvals)
                .FirstOrDefaultAsync(r => r.Id == runId, ct);
            return run is null ? Results.NotFound() : Results.Ok(engine.ToRunDto(run, actor));
        });

        app.MapPost("/api/runs/{runId:guid}/credential", async (
            Guid runId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            EphemeralCredentialStore credentials,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireTechnicianOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null)
            {
                return Results.NotFound();
            }

            var cred = credentials.ConsumeOnce(runId);
            if (cred is null)
            {
                return Results.Problem(
                    title: "Credential already shown",
                    detail: "Credential already shown; issue a new one.",
                    statusCode: StatusCodes.Status410Gone);
            }

            run.CredentialViewed = true;
            await db.SaveChangesAsync(ct);

            var formatted = cred.Kind == "Temporary Access Pass"
                ? EphemeralCredentialStore.FormatGroupedInFours(cred.Value)
                : cred.Value;

            return Results.Ok(new IssuedCredentialDto(
                cred.Kind,
                cred.UserPrincipalName,
                formatted,
                cred.ExpiresAt,
                OneTimeOnly: true));
        });

        app.MapPost("/api/runs/{runId:guid}/retry", async (
            Guid runId,
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

            var run = await engine.RetryRunAsync(runId, actor!, ct);
            return Results.Ok(engine.ToRunDto(run, actor));
        });

        app.MapPost("/api/runs/{runId:guid}/cancel", async (
            Guid runId,
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

            var run = await engine.CancelRunAsync(runId, actor!, ct);
            return Results.Ok(engine.ToRunDto(run, actor));
        });

        // --- Approvals & Approval Rules (M3) ------------------------------------------------

        app.MapGet("/api/approvals", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var pending = await db.WorkflowRuns.AsNoTracking()
                .Include(r => r.Steps)
                .Include(r => r.Approvals)
                .Where(r => r.Status == WorkflowRunStatus.AwaitingApproval)
                .OrderBy(r => r.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(pending.Select(r => engine.ToRunDto(r, actor)).ToList());
        });

        app.MapPost("/api/approvals/{runId:guid}/decision", async (
            Guid runId,
            ApprovalDecisionRequest request,
            HttpContext ctx,
            PortalSessionService session,
            WorkflowEngine engine,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireApproverOrAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            try
            {
                var run = await engine.DecideApprovalAsync(runId, request, actor!, ct);
                return Results.Ok(engine.ToRunDto(run, actor));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "Approval not allowed", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(title: "Comment required", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapGet("/api/approval-rules", async (
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            return Results.Ok(await settings.GetApprovalRulesAsync(ct));
        });

        app.MapPut("/api/approval-rules/{actionType}", async (
            WorkflowRunType actionType,
            UpdateApprovalRuleRequest request,
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var updated = await settings.UpdateApprovalRuleAsync(actionType, request, ct);
            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "approval_rule.updated", actionType.ToString(), "succeeded",
                request, summary: $"Updated approval rule for {actionType}: required={updated.Required}.", cancellationToken: ct);
            return Results.Ok(updated);
        });

        // --- Audit Log (M3, M10) ------------------------------------------------------------

        app.MapGet("/api/audit", async (
            HttpContext ctx,
            PortalSessionService session,
            AuditService audit,
            string? actor,
            string? action,
            string? target,
            string? outcome,
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? cursor,
            int? limit,
            CancellationToken ct) =>
        {
            var (current, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            // Section 4.5: Technicians see only their own actions.
            var restrictToUpn = RoleAuthorizationPolicy.CanViewAllAuditEvents(current!.Roles) ? null : current.Upn;
            long? cursorId = long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

            var page = await audit.QueryAsync(actor, action, target, outcome, from, to, restrictToUpn, cursorId, limit ?? 50, ct);
            return Results.Ok(page);
        });

        app.MapGet("/api/audit/export", async (
            HttpContext ctx,
            PortalSessionService session,
            AuditService audit,
            string? actor,
            string? action,
            string? target,
            string? outcome,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken ct) =>
        {
            var (current, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var restrictToUpn = RoleAuthorizationPolicy.CanViewAllAuditEvents(current!.Roles) ? null : current.Upn;
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = "attachment; filename=\"rollcall-audit.csv\"";
            await audit.WriteCsvAsync(ctx.Response.Body, actor, action, target, outcome, from, to, restrictToUpn, ct);
            return Results.Empty;
        });

        app.MapPost("/api/audit/verify", async (
            HttpContext ctx,
            PortalSessionService session,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var result = await audit.VerifyChainAsync(ct);
            return Results.Ok(result);
        });
    }

    private static async Task<TemplateValidationResultDto> ValidateTemplateAgainstTenantAsync(
        SaveRoleTemplateRequest request,
        IGraphClient graph,
        CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("Template name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Definition.UpnPattern))
        {
            errors.Add("UPN pattern is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Definition.DisplayNamePattern))
        {
            errors.Add("Display name pattern is required.");
        }

        var groups = await graph.ListGroupsAsync(null, ct);
        var groupIds = groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gid in request.Definition.CloudGroupIds)
        {
            if (!groupIds.Contains(gid))
            {
                errors.Add($"Group \"{gid}\" was not found in Entra ID.");
            }
        }

        var skus = await graph.ListSubscribedSkusAsync(ct);
        var skuMap = skus.ToDictionary(s => s.SkuId, StringComparer.OrdinalIgnoreCase);
        foreach (var lic in request.Definition.Licenses)
        {
            if (!skuMap.TryGetValue(lic.SkuId, out var sku))
            {
                errors.Add($"License SKU \"{lic.SkuId}\" was not found in this tenant.");
            }
            else if (sku.AvailableSeats == 0)
            {
                warnings.Add($"License {sku.FriendlyName} currently has 0 available seats.");
            }
        }

        return new TemplateValidationResultDto(errors.Count == 0, errors, warnings);
    }

    private static async Task<WelcomePackViewDto?> BuildWelcomePackAsync(
        Guid runId,
        RollcallDbContext db,
        SettingsService settings,
        EphemeralCredentialStore credentials,
        bool consumeCredential,
        CancellationToken ct)
    {
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return null;
        }

        var branding = await settings.GetWelcomePackSettingsAsync(ct);
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(branding.OrganizationName) && tenant is not null)
        {
            branding = branding with { OrganizationName = tenant.OrganizationName };
        }

        EphemeralCredential? cred = null;
        if (consumeCredential)
        {
            cred = credentials.ConsumeOnce(runId);
            if (cred is not null)
            {
                run.CredentialViewed = true;
                await db.SaveChangesAsync(ct);
            }
        }

        var formattedValue = cred is null
            ? null
            : cred.Kind == "Temporary Access Pass"
                ? EphemeralCredentialStore.FormatGroupedInFours(cred.Value)
                : cred.Value;

        return new WelcomePackViewDto(
            run.Id,
            run.TargetUserDisplayName ?? run.TargetUserRef,
            run.TargetUserRef,
            run.ScheduledFor,
            CredentialAvailable: cred is not null,
            CredentialKind: cred?.Kind ?? run.CredentialKind,
            CredentialValue: formattedValue,
            CredentialExpiresAt: cred?.ExpiresAt ?? run.CredentialExpiresAt,
            CredentialAlreadyShownMessage: cred is null && (run.CredentialViewed || !string.IsNullOrWhiteSpace(run.CredentialKind))
                ? "Credential already shown; issue a new one."
                : null,
            Branding: branding);
    }

    private static RoleTemplateDto ToTemplateDto(RoleTemplateEntity e) => new(
        e.Id,
        e.Name,
        e.Description,
        e.Version,
        e.IsArchived,
        e.ETag,
        JsonSerializer.Deserialize<RoleTemplateDefinition>(e.DefinitionJson, JsonOptions)!,
        e.CreatedAt,
        e.UpdatedAt,
        e.UpdatedByUpn);

    private static string ComputeETag(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
}
