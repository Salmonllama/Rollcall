// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Rollcall.Api.Auth;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Api.Endpoints;

public static class SetupAndAuthEndpoints
{
    public static void MapSetupAndAuthEndpoints(this WebApplication app)
    {
        // --- Setup Wizard (M2: returns 404 when setup is complete or token missing) ---------

        app.MapPost("/api/setup/verify-token", async (
            VerifySetupTokenRequest request,
            HttpContext ctx,
            SettingsService settings,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct))
            {
                return Results.NotFound();
            }

            var configuredToken = config["ROLLCALL_SETUP_TOKEN"]
                ?? config["Rollcall:SetupToken"]
                ?? Environment.GetEnvironmentVariable("ROLLCALL_SETUP_TOKEN");
            if (string.IsNullOrWhiteSpace(configuredToken) || string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.NotFound();
            }

            var valid = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(request.Token.Trim()),
                Encoding.UTF8.GetBytes(configuredToken.Trim()));
            if (!valid)
            {
                return Results.Problem(
                    title: "Invalid setup token",
                    detail: "That setup token doesn't match /opt/rollcall/.env. Copy it from the installer output and try again.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            PortalSessionService.SetSetupSessionCookie(ctx, request.Token);
            return Results.Ok(new { verified = true });
        });

        app.MapGet("/api/setup/status", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            AppCertificateService certService,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct))
            {
                return Results.NotFound();
            }

            if (!PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var hostname = config["ROLLCALL_HOSTNAME"] ?? ctx.Request.Host.Host;
            var cert = await db.AppCertificates.AsNoTracking()
                .Where(c => c.Status == AppCertificateStatus.Pending || c.Status == AppCertificateStatus.Active)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);

            return Results.Ok(new SetupStatusDto(
                IsCompleted: false,
                HasTokenSession: true,
                HasCertificate: cert is not null,
                HasTenantConnection: tenant is not null,
                Hostname: hostname,
                RedirectUri: $"https://{hostname}/signin-oidc",
                PendingCertificate: cert is null ? null : certService.ToSummary(cert),
                ConnectedTenantId: tenant?.TenantId,
                ConnectedClientId: tenant?.AppClientId,
                PrimaryDomain: tenant?.PrimaryDomain));
        });

        app.MapPost("/api/setup/certificate", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            AppCertificateService certService,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct) || !PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var hostname = config["ROLLCALL_HOSTNAME"] ?? ctx.Request.Host.Host;
            var cert = certService.GenerateCertificate(hostname);
            db.AppCertificates.Add(cert);
            await db.SaveChangesAsync(ct);

            // Private key is NEVER included in any response (M2 criterion 2).
            return Results.Ok(certService.ToSummary(cert));
        });

        app.MapGet("/api/setup/certificate/download", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct) || !PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var cert = await db.AppCertificates.AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cert is null)
            {
                return Results.NotFound();
            }

            return Results.File(cert.PublicCertificateDer, "application/pkix-cert", "rollcall-app.cer");
        });

        app.MapGet("/api/setup/app-registration-guide", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            IEnumerable<IWorkflowStep> steps,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct) || !PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var cert = await db.AppCertificates.AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (cert is null)
            {
                return Results.BadRequest(new { detail = "Generate the app certificate first." });
            }

            var hostname = config["ROLLCALL_HOSTNAME"] ?? ctx.Request.Host.Host;
            var redirectUri = $"https://{hostname}/signin-oidc";
            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var manifest = PermissionManifestBuilder.Build(steps, portalSettings.Features);
            var script = AppRegistrationScriptGenerator.GenerateSetupScript(hostname, redirectUri, cert.PublicCertificateDer, manifest);
            var dirRoles = manifest.Entries
                .Where(e => e.IsEnabled && !string.IsNullOrWhiteSpace(e.DirectoryRole))
                .Select(e => e.DirectoryRole!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return Results.Ok(new AppRegistrationGuideDto(
                hostname,
                redirectUri,
                cert.Thumbprint,
                script,
                manifest.Entries,
                dirRoles));
        });

        app.MapPost("/api/setup/validate-connection", async (
            ValidateConnectionRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            AppCertificateService certService,
            IGraphClient graph,
            IEnumerable<IWorkflowStep> steps,
            IConfiguration config,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct) || !PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var certEntity = await db.AppCertificates
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (certEntity is null)
            {
                return Results.BadRequest(new { detail = "Generate the app certificate before validating the tenant connection." });
            }

            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var manifest = PermissionManifestBuilder.Build(steps, portalSettings.Features);

            try
            {
                using var cert = certService.LoadInMemory(certEntity);
                var org = await graph.ValidateConnectionAsync(request.TenantId.Trim(), request.ClientId.Trim(), cert, ct);

                var (permChecks, roleChecks) = BuildPermissionChecks(manifest, org);

                // Save/update tenant connection and activate the certificate
                var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
                if (tenant is null)
                {
                    tenant = new TenantEntity
                    {
                        Id = Guid.NewGuid(),
                        TenantId = request.TenantId.Trim(),
                        AppClientId = request.ClientId.Trim(),
                        PrimaryDomain = string.IsNullOrWhiteSpace(request.PrimaryDomain) ? org.PrimaryDomain : request.PrimaryDomain.Trim(),
                        OrganizationName = org.DisplayName,
                        ConnectedAt = clock.GetUtcNow(),
                    };
                    db.Tenants.Add(tenant);
                }
                else
                {
                    tenant.TenantId = request.TenantId.Trim();
                    tenant.AppClientId = request.ClientId.Trim();
                    tenant.PrimaryDomain = string.IsNullOrWhiteSpace(request.PrimaryDomain) ? org.PrimaryDomain : request.PrimaryDomain.Trim();
                    tenant.OrganizationName = org.DisplayName;
                    tenant.ConnectedAt = clock.GetUtcNow();
                }

                certEntity.Status = AppCertificateStatus.Active;
                await db.SaveChangesAsync(ct);

                return Results.Ok(new ConnectionValidationResultDto(
                    Succeeded: true,
                    TenantId: tenant.TenantId,
                    DisplayName: tenant.OrganizationName,
                    PrimaryDomain: tenant.PrimaryDomain,
                    Error: null,
                    Permissions: permChecks,
                    DirectoryRoles: roleChecks));
            }
            catch (StepExecutionException ex)
            {
                return Results.Ok(new ConnectionValidationResultDto(
                    Succeeded: false,
                    TenantId: request.TenantId,
                    DisplayName: null,
                    PrimaryDomain: request.PrimaryDomain,
                    Error: ex.PlainLanguageMessage,
                    Permissions: [],
                    DirectoryRoles: []));
            }
        });

        app.MapPost("/api/setup/complete", async (
            CompleteSetupRequest? request,
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            PortalSessionService sessionService,
            AuditService audit,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (await settings.IsSetupCompletedAsync(ct) || !PortalSessionService.HasValidSetupToken(ctx, config))
            {
                return Results.NotFound();
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return Results.BadRequest(new { detail = "Connect a Microsoft 365 tenant before finishing setup." });
            }

            PortalUserEntity? firstAdmin = null;
            if (ctx.User.Identity?.IsAuthenticated == true)
            {
                firstAdmin = await sessionService.ResolveCurrentUserAsync(ctx.User, ct);
                if (firstAdmin is not null && !firstAdmin.GetRoles().Contains(PortalRole.Admin))
                {
                    firstAdmin.SetRoles(firstAdmin.GetRoles().Append(PortalRole.Admin));
                    await db.SaveChangesAsync(ct);
                }
            }
            else if (request is not null && !string.IsNullOrWhiteSpace(request.FirstAdminUpn))
            {
                firstAdmin = await sessionService.SignInUserAsync(
                    ctx,
                    request.FirstAdminObjectId ?? Guid.NewGuid().ToString("D"),
                    request.FirstAdminUpn.Trim(),
                    request.FirstAdminDisplayName ?? request.FirstAdminUpn.Trim(),
                    grantAdminIfFirstUser: true,
                    ct);
            }

            if (firstAdmin is null)
            {
                return Results.BadRequest(new { detail = "Sign in with your Microsoft work account to become the first Admin." });
            }

            await settings.MarkSetupCompletedAsync(ct);
            ctx.Response.Cookies.Delete(PortalAuthConstants.SetupTokenCookie);

            await audit.AppendAsync(
                firstAdmin.Upn,
                firstAdmin.DisplayName,
                "setup.completed",
                tenant.TenantId,
                "succeeded",
                new { tenant.TenantId, tenant.PrimaryDomain, firstAdmin.Upn },
                summary: $"Completed Rollcall setup for {tenant.PrimaryDomain} with first Admin {firstAdmin.Upn}.",
                cancellationToken: ct);

            return Results.Ok(new { completed = true, adminUpn = firstAdmin.Upn });
        });

        // --- Authentication & Current User --------------------------------------------------

        app.MapGet("/api/me", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            PortalSessionService sessionService,
            AppCertificateService certService,
            CancellationToken ct) =>
        {
            var setupCompleted = await settings.IsSetupCompletedAsync(ct);
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var portalSettings = await settings.GetPortalSettingsAsync(ct);
            var user = await sessionService.ResolveCurrentUserAsync(ctx.User, ct);

            var activeCert = await db.AppCertificates.AsNoTracking()
                .Where(c => c.Status == AppCertificateStatus.Active)
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefaultAsync(ct);
            var roles = user is not null && !user.Disabled ? user.GetRoles() : [];
            var showExpiryWarning = roles.Contains(PortalRole.Admin) && certService.ShouldWarnExpiry(activeCert);
            var certDays = activeCert is not null ? certService.ToSummary(activeCert).DaysUntilExpiry : (int?)null;

            ctx.Request.Cookies.TryGetValue(PortalAuthConstants.CsrfCookie, out var csrf);

            return Results.Ok(new CurrentUserDto(
                IsAuthenticated: user is not null,
                SetupCompleted: setupCompleted,
                Id: user?.Id,
                EntraObjectId: user?.EntraObjectId,
                Upn: user?.Upn,
                DisplayName: user?.DisplayName,
                Roles: roles,
                Disabled: user?.Disabled ?? false,
                TenantDomain: tenant?.PrimaryDomain,
                OrganizationName: tenant?.OrganizationName,
                Features: portalSettings.Features,
                ShowCertExpiryWarning: showExpiryWarning,
                CertDaysUntilExpiry: certDays,
                CsrfToken: csrf));
        });

        app.MapGet("/api/auth/login", async (
            HttpContext ctx,
            RollcallDbContext db,
            PortalSessionService sessionService,
            string? returnUrl,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return Results.Redirect("/setup");
            }

            var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var safeReturn = !string.IsNullOrWhiteSpace(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
                ? returnUrl
                : "/";

            var statePayload = $"{state}|{verifier}|{safeReturn}";
            ctx.Response.Cookies.Append(PortalAuthConstants.OidcStateCookie, statePayload, new CookieOptions
            {
                HttpOnly = true,
                Secure = ctx.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                MaxAge = TimeSpan.FromMinutes(15),
            });

            var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-oidc";
            var authority = sessionService.GetOidcAuthority(tenant.TenantId);
            var authorizeUrl = $"{authority}/oauth2/v2.0/authorize"
                + $"?client_id={Uri.EscapeDataString(tenant.AppClientId)}"
                + "&response_type=code"
                + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
                + "&scope=openid%20profile%20email"
                + $"&state={Uri.EscapeDataString(state)}"
                + $"&code_challenge={Uri.EscapeDataString(challenge)}"
                + "&code_challenge_method=S256";

            return Results.Redirect(authorizeUrl);
        });

        app.MapGet("/signin-oidc", async (
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            PortalSessionService sessionService,
            AuditService audit,
            string? code,
            string? state,
            string? error,
            string? error_description,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                return Results.Problem(
                    title: "Sign-in failed",
                    detail: error_description ?? error,
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            if (tenant is null || string.IsNullOrWhiteSpace(code))
            {
                return Results.BadRequest(new { detail = "Missing authorization code or tenant configuration." });
            }

            if (!ctx.Request.Cookies.TryGetValue(PortalAuthConstants.OidcStateCookie, out var stateCookie))
            {
                return Results.BadRequest(new { detail = "Missing OIDC state cookie. Start sign-in again." });
            }

            ctx.Response.Cookies.Delete(PortalAuthConstants.OidcStateCookie);
            var parts = stateCookie.Split('|', 3);
            if (parts.Length != 3 || !string.Equals(parts[0], state, StringComparison.Ordinal))
            {
                return Results.BadRequest(new { detail = "OIDC state mismatch. Start sign-in again." });
            }

            var verifier = parts[1];
            var returnUrl = parts[2];
            var redirectUri = $"{ctx.Request.Scheme}://{ctx.Request.Host}/signin-oidc";

            var idToken = await sessionService.ExchangeAuthorizationCodeAsync(code, verifier, redirectUri, ct);
            var validation = await sessionService.ValidateSingleTenantIdTokenAsync(idToken, tenant.TenantId, ct);
            if (!validation.Valid)
            {
                return Results.Problem(
                    title: "Tenant sign-in rejected",
                    detail: validation.Error,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var setupCompleted = await settings.IsSetupCompletedAsync(ct);
            var user = await sessionService.SignInUserAsync(
                ctx,
                validation.Oid!,
                validation.Upn!,
                validation.DisplayName!,
                grantAdminIfFirstUser: !setupCompleted,
                ct);

            await audit.AppendAsync(
                user.Upn,
                user.DisplayName,
                "auth.signed_in",
                user.Upn,
                "succeeded",
                new { user.EntraObjectId, roles = user.GetRoles() },
                summary: $"{user.DisplayName} signed in with Entra ID.",
                cancellationToken: ct);

            if (!setupCompleted)
            {
                return Results.Redirect("/setup?step=5");
            }

            return Results.Redirect(user.GetRoles().Count == 0 ? "/no-access" : returnUrl);
        });

        // Direct ID-token sign-in endpoint used by integration tests and OIDC token validation tests (M2 criterion 5).
        app.MapPost("/api/auth/oidc-token", async (
            OidcTokenExchangeRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            SettingsService settings,
            PortalSessionService sessionService,
            CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return Results.BadRequest(new { detail = "No tenant is connected." });
            }

            var validation = await sessionService.ValidateSingleTenantIdTokenAsync(request.IdToken, tenant.TenantId, ct);
            if (!validation.Valid)
            {
                return Results.Problem(
                    title: "Tenant sign-in rejected",
                    detail: validation.Error,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var setupCompleted = await settings.IsSetupCompletedAsync(ct);
            var user = await sessionService.SignInUserAsync(
                ctx,
                validation.Oid!,
                validation.Upn!,
                validation.DisplayName!,
                grantAdminIfFirstUser: !setupCompleted,
                ct);

            return Results.Ok(new { user.Id, user.Upn, roles = user.GetRoles() });
        });

        app.MapPost("/api/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(PortalAuthConstants.CookieScheme);
            ctx.Response.Cookies.Delete(PortalAuthConstants.CsrfCookie);
            return Results.Ok(new { signedOut = true });
        });
    }

    public static (List<PermissionCheckItemDto> Permissions, List<PermissionCheckItemDto> DirectoryRoles) BuildPermissionChecks(
        PermissionManifestDto manifest,
        GraphOrganizationInfo org)
    {
        var grantedPerms = new HashSet<string>(org.GrantedAppPermissions, StringComparer.OrdinalIgnoreCase);
        var grantedRoles = new HashSet<string>(org.AssignedDirectoryRoles, StringComparer.OrdinalIgnoreCase);

        var permChecks = manifest.Entries
            .Where(e => e.IsEnabled)
            .GroupBy(e => (e.Api, e.Permission))
            .Select(g => new PermissionCheckItemDto(
                Name: g.Key.Permission,
                Api: g.Key.Api,
                Kind: "ApplicationPermission",
                Granted: grantedPerms.Contains(g.Key.Permission),
                AffectedFeatures: g.Select(x => x.Feature).Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(p => p.Api, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var roleChecks = manifest.Entries
            .Where(e => e.IsEnabled && !string.IsNullOrWhiteSpace(e.DirectoryRole))
            .GroupBy(e => e.DirectoryRole!)
            .Select(g => new PermissionCheckItemDto(
                Name: g.Key,
                Api: "Microsoft Entra ID",
                Kind: "DirectoryRole",
                Granted: grantedRoles.Contains(g.Key),
                AffectedFeatures: g.Select(x => x.Feature).Distinct(StringComparer.Ordinal).ToList()))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

        return (permChecks, roleChecks);
    }

    public sealed record OidcTokenExchangeRequest(string IdToken);
}
