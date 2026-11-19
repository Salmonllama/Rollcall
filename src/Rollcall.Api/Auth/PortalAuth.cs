// SPDX-License-Identifier: GPL-3.0-or-later

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Api.Auth;

public static class PortalAuthConstants
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string SetupTokenCookie = "rc_setup_session";
    public const string CsrfCookie = "rc_csrf";
    public const string CsrfHeader = "X-CSRF-TOKEN";
    public const string OidcStateCookie = "rc_oidc_state";
    public const string PolicyAnyPortalRole = "AnyPortalRole";
    public const string PolicyRunWorkflows = "RunWorkflows";
    public const string PolicyApproveWorkflows = "ApproveWorkflows";
    public const string PolicyManageConditionalAccess = "ManageConditionalAccess";
    public const string PolicyAdminOnly = "AdminOnly";
}

/// <summary>
/// Validates Entra ID single-tenant tokens (rejecting foreign tid — M2 criterion 5),
/// manages session cookies (8h absolute, 1h idle), and resolves live PortalUser roles.
/// </summary>
public sealed class PortalSessionService(
    RollcallDbContext db,
    AppCertificateService certService,
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    TimeProvider clock)
{
    public async Task<PortalUserEntity?> ResolveCurrentUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var upn = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst(ClaimTypes.Upn)?.Value
            ?? principal.FindFirst(ClaimTypes.Email)?.Value
            ?? principal.FindFirst(ClaimTypes.Name)?.Value;
        var oid = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrWhiteSpace(upn) && string.IsNullOrWhiteSpace(oid))
        {
            return null;
        }

        PortalUserEntity? user = null;
        if (!string.IsNullOrWhiteSpace(oid))
        {
            user = await db.PortalUsers.FirstOrDefaultAsync(u => u.EntraObjectId == oid, cancellationToken);
        }

        if (user is null && !string.IsNullOrWhiteSpace(upn))
        {
            user = await db.PortalUsers.FirstOrDefaultAsync(u => EF.Functions.ILike(u.Upn, upn), cancellationToken);
        }

        return user;
    }

    public async Task<(bool Valid, string? Error, string? Oid, string? Upn, string? DisplayName)> ValidateSingleTenantIdTokenAsync(
        string idToken,
        string expectedTenantId,
        CancellationToken cancellationToken)
    {
        var handler = new JwtSecurityTokenHandler();
        if (!handler.CanReadToken(idToken))
        {
            return (false, "Invalid ID token format.", null, null, null);
        }

        var jwt = handler.ReadJwtToken(idToken);
        var tokenTid = jwt.Claims.FirstOrDefault(c => string.Equals(c.Type, "tid", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(tokenTid) || !string.Equals(tokenTid, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Only accounts from the connected Microsoft 365 tenant can sign in.", null, null, null);
        }

        var oid = jwt.Claims.FirstOrDefault(c => c.Type is "oid" or "sub")?.Value ?? Guid.NewGuid().ToString("D");
        var upn = jwt.Claims.FirstOrDefault(c => c.Type is "preferred_username" or "upn" or "email")?.Value;
        var name = jwt.Claims.FirstOrDefault(c => c.Type == "name")?.Value ?? upn;

        if (string.IsNullOrWhiteSpace(upn))
        {
            return (false, "ID token did not include a user principal name.", null, null, null);
        }

        // Verify signature against configured OIDC authority JWKS when live/fake OIDC JWKS is reachable.
        var authority = GetOidcAuthority(expectedTenantId);
        var skipSig = string.Equals(configuration["Oidc:SkipSignatureValidationForUnitTests"], "true", StringComparison.OrdinalIgnoreCase);
        if (!skipSig)
        {
            try
            {
                var client = httpClientFactory.CreateClient("RollcallGraph");
                var discovery = await client.GetStringAsync(new Uri($"{authority}/.well-known/openid-configuration"), cancellationToken);
                using var discDoc = JsonDocument.Parse(discovery);
                if (discDoc.RootElement.TryGetProperty("jwks_uri", out var jwksUriEl) && jwksUriEl.GetString() is { } jwksUri)
                {
                    var jwksJson = await client.GetStringAsync(new Uri(jwksUri), cancellationToken);
                    var jwks = new JsonWebKeySet(jwksJson);
                    var validationParams = new TokenValidationParameters
                    {
                        ValidateIssuer = false,
                        ValidateAudience = false,
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKeys = jwks.GetSigningKeys(),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(5),
                    };
                    handler.ValidateToken(idToken, validationParams, out _);
                }
            }
            catch (SecurityTokenException ex)
            {
                return (false, $"ID token signature or lifetime validation failed: {ex.Message}", null, null, null);
            }
            catch (HttpRequestException)
            {
                return (false, "Couldn't reach the OIDC identity provider to verify the sign-in token.", null, null, null);
            }
        }

        return (true, null, oid, upn, name);
    }

    public async Task<string> ExchangeAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Rollcall is not connected to a tenant.");
        var activeCert = await db.AppCertificates.AsNoTracking()
            .Where(c => c.Status == AppCertificateStatus.Active)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("No active app certificate is available.");

        var authority = GetOidcAuthority(tenant.TenantId);
        var tokenEndpoint = $"{authority}/oauth2/v2.0/token";

        using var cert = certService.LoadInMemory(activeCert);
        var now = clock.GetUtcNow().UtcDateTime;
        var creds = new SigningCredentials(new X509SecurityKey(cert), SecurityAlgorithms.RsaSha256);
        var header = new JwtHeader(creds);
        header["x5t"] = Base64UrlEncoder.Encode(cert.GetCertHash(HashAlgorithmName.SHA1));
        var assertionToken = new JwtSecurityToken(
            header,
            new JwtPayload(
                issuer: tenant.AppClientId,
                audience: tokenEndpoint,
                claims:
                [
                    new Claim(JwtRegisteredClaimNames.Sub, tenant.AppClientId),
                    new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                ],
                notBefore: now.AddMinutes(-1),
                expires: now.AddMinutes(5)));
        var assertion = new JwtSecurityTokenHandler().WriteToken(assertionToken);

        var client = httpClientFactory.CreateClient("RollcallGraph");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = tenant.AppClientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        });

        using var response = await client.PostAsync(new Uri(tokenEndpoint), form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OIDC code exchange failed with HTTP {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id_token").GetString()
            ?? throw new InvalidOperationException("OIDC response did not include an id_token.");
    }

    public string GetOidcAuthority(string tenantId)
    {
        var configured = configuration["Oidc:Authority"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        return $"https://login.microsoftonline.com/{tenantId}/v2.0";
    }

    public async Task<PortalUserEntity> SignInUserAsync(
        HttpContext httpContext,
        string oid,
        string upn,
        string displayName,
        bool grantAdminIfFirstUser,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var user = await db.PortalUsers.FirstOrDefaultAsync(
            u => u.EntraObjectId == oid || EF.Functions.ILike(u.Upn, upn),
            cancellationToken);

        var anyExisting = await db.PortalUsers.AnyAsync(cancellationToken);

        if (user is null)
        {
            user = new PortalUserEntity
            {
                Id = Guid.NewGuid(),
                EntraObjectId = oid,
                Upn = upn,
                DisplayName = displayName,
                CreatedAt = now,
                LastSignInAt = now,
            };
            if (grantAdminIfFirstUser && !anyExisting)
            {
                user.SetRoles([PortalRole.Admin]);
            }

            db.PortalUsers.Add(user);
        }
        else
        {
            user.EntraObjectId = oid;
            user.Upn = upn;
            user.DisplayName = displayName;
            user.LastSignInAt = now;
            if (grantAdminIfFirstUser && !user.GetRoles().Contains(PortalRole.Admin))
            {
                user.SetRoles(user.GetRoles().Append(PortalRole.Admin));
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString("D")),
            new("oid", user.EntraObjectId),
            new("preferred_username", user.Upn),
            new(ClaimTypes.Name, user.DisplayName),
        };
        foreach (var role in user.GetRoles())
        {
            claims.Add(new Claim(ClaimTypes.Role, role.ToString()));
        }

        var identity = new ClaimsIdentity(claims, PortalAuthConstants.CookieScheme);
        var props = new AuthenticationProperties
        {
            IsPersistent = true,
            IssuedUtc = now,
            ExpiresUtc = now.AddHours(8),
        };

        await httpContext.SignInAsync(PortalAuthConstants.CookieScheme, new ClaimsPrincipal(identity), props);

        var csrf = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        httpContext.Response.Cookies.Append(PortalAuthConstants.CsrfCookie, csrf, new CookieOptions
        {
            HttpOnly = false,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = now.AddHours(8),
        });

        return user;
    }

    public static bool HasValidSetupToken(HttpContext context, IConfiguration configuration)
    {
        var configuredToken = configuration["ROLLCALL_SETUP_TOKEN"]
            ?? configuration["Rollcall:SetupToken"]
            ?? Environment.GetEnvironmentVariable("ROLLCALL_SETUP_TOKEN");
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return false;
        }

        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken.Trim())));
        if (context.Request.Cookies.TryGetValue(PortalAuthConstants.SetupTokenCookie, out var cookieHash) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookieHash), Encoding.UTF8.GetBytes(expectedHash)))
        {
            return true;
        }

        if (context.Request.Headers.TryGetValue("X-Setup-Token", out var headerToken) &&
            CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(headerToken.ToString().Trim()),
                Encoding.UTF8.GetBytes(configuredToken.Trim())))
        {
            return true;
        }

        return false;
    }

    public static void SetSetupSessionCookie(HttpContext context, string token)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));
        context.Response.Cookies.Append(PortalAuthConstants.SetupTokenCookie, hash, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            MaxAge = TimeSpan.FromHours(2),
        });
    }
}

public static class EndpointAuthHelpers
{
    public static async Task<(ActorIdentity? Actor, IResult? ErrorResult)> RequireAsync(
        HttpContext httpContext,
        PortalSessionService sessionService,
        Func<IReadOnlyList<PortalRole>, bool> predicate,
        string deniedMessage,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return (null, Results.Problem(
                title: "Sign-in required",
                detail: "Sign in with your Microsoft work account to continue.",
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var user = await sessionService.ResolveCurrentUserAsync(httpContext.User, cancellationToken);
        if (user is null || user.Disabled)
        {
            return (null, Results.Problem(
                title: "Access denied",
                detail: "Your account doesn't have access to Rollcall. Ask an admin to grant a role.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        var roles = user.GetRoles();
        if (roles.Count == 0 || !predicate(roles))
        {
            return (null, Results.Problem(
                title: "Permission required",
                detail: deniedMessage,
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (new ActorIdentity(user.EntraObjectId, user.Upn, user.DisplayName, roles), null);
    }

    public static Task<(ActorIdentity? Actor, IResult? ErrorResult)> RequireAnyRoleAsync(
        HttpContext ctx, PortalSessionService s, CancellationToken ct) =>
        RequireAsync(ctx, s, RoleAuthorizationPolicy.CanViewPortal, "Ask an admin for a Rollcall role to view the portal.", ct);

    public static Task<(ActorIdentity? Actor, IResult? ErrorResult)> RequireTechnicianOrAdminAsync(
        HttpContext ctx, PortalSessionService s, CancellationToken ct) =>
        RequireAsync(ctx, s, RoleAuthorizationPolicy.CanRunStandardWorkflows, "You need the Technician or Admin role for this action.", ct);

    public static Task<(ActorIdentity? Actor, IResult? ErrorResult)> RequireApproverOrAdminAsync(
        HttpContext ctx, PortalSessionService s, CancellationToken ct) =>
        RequireAsync(ctx, s, RoleAuthorizationPolicy.CanApprove, "You need the Approver or Admin role for this action.", ct);

    public static Task<(ActorIdentity? Actor, IResult? ErrorResult)> RequireAdminAsync(
        HttpContext ctx, PortalSessionService s, CancellationToken ct) =>
        RequireAsync(ctx, s, RoleAuthorizationPolicy.CanAdminister, "You need the Admin role for this action.", ct);
}
