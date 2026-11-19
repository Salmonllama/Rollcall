// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Api.Auth;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Api.Endpoints;

public static class AdminEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void MapAdminEndpoints(this WebApplication app)
    {
        // --- Permission Manifest (4.6) ------------------------------------------------------

        app.MapGet("/api/permissions/manifest", async (
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            IEnumerable<IWorkflowStep> steps,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAnyRoleAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            return Results.Ok(PermissionManifestBuilder.Build(steps, portalSettings.Features));
        });

        // --- Admin › Team (M2) --------------------------------------------------------------

        app.MapGet("/api/admin/team", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var users = await db.PortalUsers.AsNoTracking().OrderBy(u => u.Upn).ToListAsync(ct);
            return Results.Ok(users.Select(ToPortalUserDto).ToList());
        });

        app.MapPost("/api/admin/team", async (
            InvitePortalUserRequest request,
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

            var normalizedUpn = request.Upn.Trim();
            var existing = await db.PortalUsers.FirstOrDefaultAsync(u => EF.Functions.ILike(u.Upn, normalizedUpn), ct);
            if (existing is not null)
            {
                existing.SetRoles(request.Roles);
                existing.Disabled = false;
                if (!string.IsNullOrWhiteSpace(request.DisplayName))
                {
                    existing.DisplayName = request.DisplayName.Trim();
                }

                await db.SaveChangesAsync(ct);
                return Results.Ok(ToPortalUserDto(existing));
            }

            var user = new PortalUserEntity
            {
                Id = Guid.NewGuid(),
                EntraObjectId = request.EntraObjectId?.Trim() ?? string.Empty,
                Upn = normalizedUpn,
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? normalizedUpn : request.DisplayName.Trim(),
                CreatedAt = clock.GetUtcNow(),
            };
            user.SetRoles(request.Roles);
            db.PortalUsers.Add(user);
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "portal.user.invited", user.Upn, "succeeded",
                new { user.Upn, roles = request.Roles }, summary: $"Invited {user.Upn} with roles: {string.Join(", ", request.Roles)}.", cancellationToken: ct);
            return Results.Created($"/api/admin/team/{user.Id}", ToPortalUserDto(user));
        });

        app.MapPut("/api/admin/team/{userId:guid}", async (
            Guid userId,
            UpdatePortalUserRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var user = await db.PortalUsers.FirstOrDefaultAsync(u => u.Id == userId, ct);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.SetRoles(request.Roles);
            user.Disabled = request.Disabled;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "portal.user.updated", user.Upn, "succeeded",
                new { user.Upn, roles = request.Roles, request.Disabled },
                summary: $"Updated portal access for {user.Upn}.",
                cancellationToken: ct);
            return Results.Ok(ToPortalUserDto(user));
        });

        // --- Admin › Connections & Certificate Rotation (M2, M10) ---------------------------

        app.MapGet("/api/admin/connections", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            SettingsService settings,
            AppCertificateService certService,
            IGraphClient graph,
            IEnumerable<IWorkflowStep> steps,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var certs = await db.AppCertificates.AsNoTracking().OrderByDescending(c => c.CreatedAt).ToListAsync(ct);
            var active = certs.FirstOrDefault(c => c.Status == AppCertificateStatus.Active);
            var pending = certs.FirstOrDefault(c => c.Status == AppCertificateStatus.Pending);
            var retiring = certs.FirstOrDefault(c => c.Status == AppCertificateStatus.Retiring);

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var manifest = PermissionManifestBuilder.Build(steps, portalSettings.Features);
            List<PermissionCheckItemDto> permChecks = [];
            List<PermissionCheckItemDto> roleChecks = [];
            string? lastError = null;

            if (tenant is not null && active is not null)
            {
                try
                {
                    using var cert = certService.LoadInMemory(active);
                    var org = await graph.ValidateConnectionAsync(tenant.TenantId, tenant.AppClientId, cert, ct);
                    (permChecks, roleChecks) = SetupAndAuthEndpoints.BuildPermissionChecks(manifest, org);
                }
                catch (StepExecutionException ex)
                {
                    lastError = ex.PlainLanguageMessage;
                }
            }

            return Results.Ok(new ConnectionsOverviewDto(
                tenant?.TenantId,
                tenant?.AppClientId,
                tenant?.PrimaryDomain,
                tenant?.OrganizationName,
                tenant?.ConnectedAt,
                active is null ? null : certService.ToSummary(active),
                pending is null ? null : certService.ToSummary(pending),
                retiring is null ? null : certService.ToSummary(retiring),
                certService.ShouldWarnExpiry(active),
                permChecks,
                roleChecks,
                lastError));
        });

        app.MapPost("/api/admin/connections/certificates/rotate/start", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AppCertificateService certService,
            AuditService audit,
            IConfiguration config,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return Results.BadRequest(new { detail = "Connect a tenant before rotating certificates." });
            }

            var hostname = config["ROLLCALL_HOSTNAME"] ?? ctx.Request.Host.Host;
            var newCert = certService.GenerateCertificate(hostname);
            db.AppCertificates.Add(newCert);
            await db.SaveChangesAsync(ct);

            var script = AppRegistrationScriptGenerator.GenerateRotationScript(tenant.AppClientId, newCert.PublicCertificateDer);
            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "certificate.rotation.started", newCert.Thumbprint, "succeeded",
                new { newCert.Id, newCert.Thumbprint, newCert.NotAfter },
                summary: $"Generated pending RSA-3072 certificate {newCert.Thumbprint} for rotation.",
                cancellationToken: ct);

            return Results.Ok(new RotateCertificateStartResponse(
                certService.ToSummary(newCert),
                script,
                $"/api/admin/connections/certificates/{newCert.Id}/download"));
        });

        app.MapGet("/api/admin/connections/certificates/{certId:guid}/download", async (
            Guid certId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var cert = await db.AppCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Id == certId, ct);
            return cert is null
                ? Results.NotFound()
                : Results.File(cert.PublicCertificateDer, "application/pkix-cert", $"rollcall-{cert.Thumbprint[..8]}.cer");
        });

        app.MapPost("/api/admin/connections/certificates/rotate/activate", async (
            RotateCertificateActionRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AppCertificateService certService,
            IGraphClient graph,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var pending = await db.AppCertificates.FirstOrDefaultAsync(c => c.Id == request.CertificateId, ct);
            if (tenant is null || pending is null)
            {
                return Results.NotFound();
            }

            // Validate the new certificate works against Graph before switching active (M10 criterion 1).
            using var x509 = certService.LoadInMemory(pending);
            await graph.ValidateConnectionAsync(tenant.TenantId, tenant.AppClientId, x509, ct);

            var previousActives = await db.AppCertificates
                .Where(c => c.Status == AppCertificateStatus.Active && c.Id != pending.Id)
                .ToListAsync(ct);
            foreach (var old in previousActives)
            {
                old.Status = AppCertificateStatus.Retiring;
            }

            pending.Status = AppCertificateStatus.Active;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "certificate.rotation.activated", pending.Thumbprint, "succeeded",
                new { pending.Id, pending.Thumbprint },
                summary: $"Validated and activated certificate {pending.Thumbprint}.",
                cancellationToken: ct);
            return Results.Ok(certService.ToSummary(pending));
        });

        app.MapPost("/api/admin/connections/certificates/rotate/retire", async (
            RotateCertificateActionRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AppCertificateService certService,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var cert = await db.AppCertificates.FirstOrDefaultAsync(c => c.Id == request.CertificateId, ct);
            if (cert is null)
            {
                return Results.NotFound();
            }

            if (cert.Status == AppCertificateStatus.Active)
            {
                return Results.BadRequest(new { detail = "Activate a new certificate before retiring the current active one." });
            }

            cert.Status = AppCertificateStatus.Retired;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "certificate.rotation.retired", cert.Thumbprint, "succeeded",
                new { cert.Id, cert.Thumbprint },
                summary: $"Retired old certificate {cert.Thumbprint}.",
                cancellationToken: ct);
            return Results.Ok(certService.ToSummary(cert));
        });

        // --- Admin › AD Agents (M9) ---------------------------------------------------------

        app.MapGet("/api/admin/agents", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var cutoff = clock.GetUtcNow() - AgentJobDispatcher.AgentHeartbeatMaxAge;
            var agents = await db.AdAgents.AsNoTracking().OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
            var dtos = agents.Select(a =>
            {
                var status = a.Status == AgentStatus.Connected && a.LastSeenAt < cutoff
                    ? AgentStatus.Offline
                    : a.Status;
                var updateAvailable = !string.IsNullOrWhiteSpace(a.Version) &&
                    !string.Equals(a.Version, "1.0.0", StringComparison.OrdinalIgnoreCase);
                return new AdAgentDto(a.Id, a.Name, status, a.Version, a.Hostname, updateAvailable, a.LastSeenAt, a.CreatedAt);
            }).ToList();

            return Results.Ok(dtos);
        });

        app.MapPost("/api/admin/agents/enroll-command", async (
            CreateAgentEnrollmentRequest request,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AuditService audit,
            IConfiguration config,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var now = clock.GetUtcNow();
            var token = AgentJwtService.GenerateEnrollmentToken();
            var expiresAt = now.AddHours(1);
            var agent = new AdAgentEntity
            {
                Id = Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(request.Name) ? "On-prem AD agent" : request.Name.Trim(),
                EnrollmentTokenHash = AgentJwtService.HashEnrollmentToken(token),
                EnrollmentTokenExpiresAt = expiresAt,
                EnrollmentTokenUsed = false,
                Status = AgentStatus.Pending,
                CreatedAt = now,
            };
            db.AdAgents.Add(agent);
            await db.SaveChangesAsync(ct);

            var hostname = config["ROLLCALL_HOSTNAME"] ?? ctx.Request.Host.Host;
            var portalUrl = $"https://{hostname}";
            var psCmd = $".\\install-agent.ps1 -PortalUrl '{portalUrl}' -Token '{token}' -ServiceAccount 'DOMAIN\\rollcall-gmsa$'";

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "agent.enrollment_created", agent.Name, "succeeded",
                new { agent.Id, agent.Name, expiresAt }, summary: $"Created 1-hour enrollment token for AD agent \"{agent.Name}\".", cancellationToken: ct);

            return Results.Ok(new AgentEnrollmentCommandDto(agent.Id, agent.Name, token, expiresAt, psCmd));
        });

        app.MapPost("/api/admin/agents/{agentId:guid}/revoke", async (
            Guid agentId,
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            AuditService audit,
            CancellationToken ct) =>
        {
            var (actor, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var agent = await db.AdAgents.FirstOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent is null)
            {
                return Results.NotFound();
            }

            agent.Status = AgentStatus.Revoked;
            agent.EnrollmentTokenUsed = true;
            agent.EnrollmentTokenHash = null;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "agent.revoked", agent.Name, "succeeded",
                new { agent.Id }, summary: $"Revoked AD agent \"{agent.Name}\".", cancellationToken: ct);
            return Results.Ok(new { revoked = true });
        });

        app.MapGet("/api/admin/agents/settings", async (
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            return err ?? Results.Ok(await settings.GetAdSettingsAsync(ct));
        });

        app.MapPut("/api/admin/agents/settings", async (
            AdSettingsDto request,
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

            var saved = await settings.SaveAdSettingsAsync(request, ct);
            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "ad.settings.updated", "ad_settings", "succeeded",
                saved, summary: $"Updated AD agent settings ({saved.AllowedOus.Count} allowed OUs).", cancellationToken: ct);
            return Results.Ok(saved);
        });

        app.MapGet("/api/admin/agents/ous", async (
            HttpContext ctx,
            PortalSessionService session,
            IAgentDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var json = await dispatcher.DispatchAndAwaitAsync(AdJobKinds.OuList, "{}", null, null, TimeSpan.FromSeconds(30), ct);
            var ous = string.IsNullOrWhiteSpace(json)
                ? []
                : JsonSerializer.Deserialize<List<AdOuDto>>(json, JsonOptions) ?? [];
            return Results.Ok(ous);
        });

        // --- Admin › Settings & Welcome Pack Branding (M6, M8, M10) -------------------------

        app.MapGet("/api/admin/settings", async (
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

            var dto = await settings.GetPortalSettingsAsync(ct);
            ctx.Response.Headers.ETag = $"\"{dto.ETag}\"";
            return Results.Ok(dto);
        });

        app.MapPut("/api/admin/settings", async (
            UpdatePortalSettingsRequest request,
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

            var ifMatch = ctx.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"');
            try
            {
                var saved = await settings.SavePortalSettingsAsync(request, ifMatch, ct);
                await audit.AppendAsync(actor!.Upn, actor.DisplayName, "settings.updated", "portal_settings", "succeeded",
                    saved, summary: "Updated portal settings.", cancellationToken: ct);
                ctx.Response.Headers.ETag = $"\"{saved.ETag}\"";
                return Results.Ok(saved);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "Settings modified", detail: ex.Message, statusCode: StatusCodes.Status412PreconditionFailed);
            }
        });

        app.MapGet("/api/admin/settings/welcome-pack", async (
            HttpContext ctx,
            PortalSessionService session,
            SettingsService settings,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            return err ?? Results.Ok(await settings.GetWelcomePackSettingsAsync(ct));
        });

        app.MapPut("/api/admin/settings/welcome-pack", async (
            WelcomePackSettingsDto request,
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

            var saved = await settings.SaveWelcomePackSettingsAsync(request, ct);
            await audit.AppendAsync(actor!.Upn, actor.DisplayName, "settings.welcome_pack.updated", "welcome_pack", "succeeded",
                new { saved.HideRollcallBranding, saved.HasHeaderImage, saved.HasLetterhead },
                summary: "Updated Welcome Pack branding settings.",
                cancellationToken: ct);
            return Results.Ok(saved);
        });

        app.MapPost("/api/admin/settings/welcome-pack/asset/{kind}", async (
            string kind,
            WelcomePackAssetUploadRequest request,
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

            if (kind is not ("header" or "letterhead"))
            {
                return Results.BadRequest(new { detail = "Asset kind must be \"header\" or \"letterhead\"." });
            }

            var bytes = string.IsNullOrWhiteSpace(request.Base64Data) ? [] : Convert.FromBase64String(request.Base64Data);
            await settings.SaveWelcomePackAssetAsync(kind, bytes, request.ContentType ?? "image/png", ct);
            return Results.Ok(await settings.GetWelcomePackSettingsAsync(ct));
        });

        // --- Admin › Health (M10) -----------------------------------------------------------

        app.MapGet("/api/admin/health", async (
            HttpContext ctx,
            PortalSessionService session,
            RollcallDbContext db,
            ReadinessProbe readiness,
            JobQueueService jobQueue,
            SettingsService settings,
            AppCertificateService certService,
            IGraphClient graph,
            IExchangeRunner exchange,
            IEnumerable<IWorkflowStep> steps,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (_, err) = await EndpointAuthHelpers.RequireAdminAsync(ctx, session, ct);
            if (err is not null)
            {
                return err;
            }

            var now = clock.GetUtcNow();
            var components = new List<HealthComponentDto>();

            var ready = await readiness.CheckAsync(ct);
            components.Add(new HealthComponentDto(
                "Database",
                ready.Db == "ok" && ready.Migrations == "current" ? "ok" : "error",
                ready.Db == "ok" ? $"Connected (migrations {ready.Migrations})" : "Database unreachable",
                null));

            components.Add(new HealthComponentDto(
                "Background worker",
                ready.Worker == "ok" ? "ok" : "error",
                ready.Worker == "ok" ? "Heartbeat current" : $"Worker heartbeat {ready.Worker}",
                ready.Worker == "ok" ? null : "Check container logs with: sudo bash /opt/rollcall/install.sh logs worker"));

            var queueDepth = await jobQueue.GetPendingDepthAsync(ct);
            var failed24h = await db.WorkflowRuns.CountAsync(
                r => r.Status == WorkflowRunStatus.PartiallyFailed && r.CreatedAt >= now.AddHours(-24),
                ct);

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var activeCert = await db.AppCertificates.AsNoTracking()
                .Where(c => c.Status == AppCertificateStatus.Active)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var manifest = PermissionManifestBuilder.Build(steps, portalSettings.Features);

            if (tenant is not null && activeCert is not null)
            {
                var summary = certService.ToSummary(activeCert);
                var certStatus = summary.DaysUntilExpiry <= 0 ? "error" : summary.DaysUntilExpiry <= 30 ? "warning" : "ok";
                components.Add(new HealthComponentDto(
                    "App certificate",
                    certStatus,
                    summary.DaysUntilExpiry <= 0
                        ? "Certificate expired"
                        : $"Expires in {summary.DaysUntilExpiry} days ({summary.NotAfter:MMM d, yyyy})",
                    $"Thumbprint: {summary.Thumbprint}"));

                try
                {
                    using var x509 = certService.LoadInMemory(activeCert);
                    var org = await graph.ValidateConnectionAsync(tenant.TenantId, tenant.AppClientId, x509, ct);
                    components.Add(new HealthComponentDto("Microsoft Graph", "ok", $"Reachable ({org.PrimaryDomain})", null));

                    var (permChecks, roleChecks) = SetupAndAuthEndpoints.BuildPermissionChecks(manifest, org);
                    var missing = permChecks.Concat(roleChecks).Where(c => !c.Granted).ToList();
                    components.Add(new HealthComponentDto(
                        "Permissions",
                        missing.Count == 0 ? "ok" : "error",
                        missing.Count == 0 ? "All required permissions and roles granted" : $"{missing.Count} missing permission(s) or role(s)",
                        missing.Count == 0 ? null : string.Join(", ", missing.Select(m => m.Name))));
                }
                catch (StepExecutionException ex)
                {
                    components.Add(new HealthComponentDto("Microsoft Graph", "error", "Graph check failed", ex.PlainLanguageMessage));
                }
            }
            else
            {
                components.Add(new HealthComponentDto("Microsoft Graph", "warning", "Tenant not connected yet", null));
            }

            if (portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.Mailbox, true))
            {
                var exoOk = await exchange.CheckHealthAsync(ct);
                components.Add(new HealthComponentDto(
                    "Exchange Online",
                    exoOk ? "ok" : "warning",
                    exoOk ? "Session healthy" : "Exchange session unavailable",
                    exoOk ? null : "Verify Exchange.ManageAsApp and Exchange Administrator role."));
            }

            if (portalSettings.Features.GetValueOrDefault(FeatureFlagKeys.HybridAd, false))
            {
                var cutoff = now - AgentJobDispatcher.AgentHeartbeatMaxAge;
                var agents = await db.AdAgents.AsNoTracking().Where(a => a.Status != AgentStatus.Revoked).ToListAsync(ct);
                var online = agents.Count(a => a.Status == AgentStatus.Connected && a.LastSeenAt >= cutoff);
                components.Add(new HealthComponentDto(
                    "AD agent",
                    online > 0 ? "ok" : "error",
                    online > 0 ? $"{online} agent(s) connected" : "No AD agent connected",
                    null));
            }

            var lastBackup = await settings.GetLastBackupAtAsync(ct);
            long? diskFree = null;
            long? diskTotal = null;
            try
            {
                var drive = new DriveInfo(AppContext.BaseDirectory);
                diskFree = drive.AvailableFreeSpace;
                diskTotal = drive.TotalSize;
            }
            catch (IOException)
            {
                // Ignore if container filesystem hides drive metrics.
            }

            var overall = components.Any(c => c.Status == "error")
                ? "unhealthy"
                : components.Any(c => c.Status == "warning")
                    ? "degraded"
                    : "healthy";

            return Results.Ok(new HealthOverviewDto(
                overall,
                now,
                Environment.GetEnvironmentVariable("ROLLCALL_VERSION") ?? "1.0.0",
                queueDepth,
                failed24h,
                lastBackup,
                diskFree,
                diskTotal,
                components));
        });
    }

    private static PortalUserDto ToPortalUserDto(PortalUserEntity u) => new(
        u.Id,
        u.EntraObjectId,
        u.Upn,
        u.DisplayName,
        u.GetRoles(),
        u.Disabled,
        u.LastSignInAt,
        u.CreatedAt);

    public sealed record WelcomePackAssetUploadRequest(string? Base64Data, string? ContentType);
}
