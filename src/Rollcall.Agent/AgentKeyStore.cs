// SPDX-License-Identifier: GPL-3.0-or-later

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Rollcall.Agent;

/// <summary>
/// Generates and loads the agent's ECDSA P-256 keypair and signs short-lived (5-minute) JWTs
/// for authenticating to the Rollcall portal (section 4.7). No shared secret is stored on either side.
/// </summary>
public sealed class AgentKeyStore : IDisposable
{
    private readonly ECDsa _ecdsa;
    private readonly TimeProvider _clock;

    public AgentKeyStore(ECDsa ecdsa, TimeProvider? clock = null)
    {
        _ecdsa = ecdsa;
        _clock = clock ?? TimeProvider.System;
    }

    public string PublicKeyPem => _ecdsa.ExportSubjectPublicKeyInfoPem();

    public static AgentKeyStore CreateNew(TimeProvider? clock = null) =>
        new(ECDsa.Create(ECCurve.NamedCurves.nistP256), clock);

    public static AgentKeyStore LoadOrCreate(string stateDirectory, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(stateDirectory);
        var keyFile = Path.Combine(stateDirectory, "agent-key.pkcs8");
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        if (File.Exists(keyFile))
        {
            var raw = File.ReadAllBytes(keyFile);
            ecdsa.ImportPkcs8PrivateKey(raw, out _);
            CryptographicOperations.ZeroMemory(raw);
            return new AgentKeyStore(ecdsa, clock);
        }

        var pkcs8 = ecdsa.ExportPkcs8PrivateKey();
        try
        {
            File.WriteAllBytes(keyFile, pkcs8);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
        }

        return new AgentKeyStore(ecdsa, clock);
    }

    public string CreateSignedJwt(Guid agentId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var key = new ECDsaSecurityKey(_ecdsa);
        var creds = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256);

        var token = new JwtSecurityToken(
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, agentId.ToString("D")),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            ],
            notBefore: now.AddSeconds(-30),
            expires: now.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void Dispose() => _ecdsa.Dispose();
}
