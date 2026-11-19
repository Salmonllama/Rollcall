// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Rollcall.Infrastructure.Security;

namespace Rollcall.Infrastructure.Workflow;

public sealed record EphemeralCredential(
    string Kind,
    string UserPrincipalName,
    string Value,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt);

/// <summary>
/// Holds one-time credentials (TAP codes and generated passwords) purely in process memory so they
/// are never persisted to PostgreSQL, logs, or audit details (M5 criterion 5, M6 criterion 6).
/// Reading a credential consumes it immediately so a second view sees "credential already shown".
/// </summary>
public sealed class EphemeralCredentialStore(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, EphemeralCredential> _store = new();

    public void Store(Guid runId, string kind, string upn, string value, DateTimeOffset? expiresAt)
    {
        SecretRedactor.RegisterSecret(value);
        _store[runId] = new EphemeralCredential(kind, upn, value, expiresAt, clock.GetUtcNow());
    }

    public bool HasCredential(Guid runId) => _store.ContainsKey(runId);

    public EphemeralCredential? ConsumeOnce(Guid runId) =>
        _store.TryRemove(runId, out var cred) ? cred : null;

    /// <summary>Generates a readable, high-entropy password avoiding confusing characters (I/l/1, O/0).</summary>
    public static string GenerateStrongPassword(int length = 16)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%&*";
        var chars = new char[length];
        var bytes = RandomNumberGenerator.GetBytes(length);
        for (var i = 0; i < length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        // Guarantee complexity (upper, lower, digit, symbol) for Entra ID and AD password policies.
        chars[0] = 'A';
        chars[1] = 'k';
        chars[2] = '7';
        chars[3] = '!';
        var password = new string(chars);
        SecretRedactor.RegisterSecret(password);
        return password;
    }

    public static string FormatGroupedInFours(string raw)
    {
        var clean = raw.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (clean.Length <= 4)
        {
            return clean;
        }

        var sb = new StringBuilder(clean.Length + (clean.Length / 4));
        for (var i = 0; i < clean.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                sb.Append(' ');
            }

            sb.Append(clean[i]);
        }

        return sb.ToString();
    }
}
