// SPDX-License-Identifier: GPL-3.0-or-later

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;

namespace Rollcall.Infrastructure.Security;

/// <summary>
/// Validates short-lived ECDSA P-256 JWTs signed by enrolled AD agents (section 4.7).
/// </summary>
public sealed class AgentJwtService(TimeProvider clock)
{
    public const int CurrentProtocolVersion = 1;
    public const int MinimumProtocolVersion = 1;

    public static string HashEnrollmentToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.Trim())));

    public static string GenerateEnrollmentToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public bool ValidateAgentJwt(string jwt, AdAgentEntity agent)
    {
        if (agent.Status == AgentStatus.Revoked || string.IsNullOrWhiteSpace(agent.PublicKeyPem))
        {
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(agent.PublicKeyPem);
            var key = new ECDsaSecurityKey(ecdsa)
            {
                KeyId = agent.Id.ToString("N"),
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };

            var handler = new JwtSecurityTokenHandler();
            var parameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(1),
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    var now = clock.GetUtcNow().UtcDateTime;
                    if (expires.HasValue && expires.Value < now.AddMinutes(-1))
                    {
                        return false;
                    }

                    if (notBefore.HasValue && notBefore.Value > now.AddMinutes(1))
                    {
                        return false;
                    }

                    return expires.HasValue && (!notBefore.HasValue || expires.Value - notBefore.Value <= TimeSpan.FromMinutes(10));
                },
            };

            var principal = handler.ValidateToken(jwt, parameters, out _);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return string.Equals(sub, agent.Id.ToString("D"), StringComparison.OrdinalIgnoreCase);
        }
        catch (SecurityTokenException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static Guid? ExtractUnvalidatedAgentId(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(jwt))
            {
                return null;
            }

            var token = handler.ReadJwtToken(jwt);
            return Guid.TryParse(token.Subject, out var id) ? id : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
