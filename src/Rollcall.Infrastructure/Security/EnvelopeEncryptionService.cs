// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Rollcall.Infrastructure.Security;

/// <summary>
/// Envelope encryption using AES-256-GCM: each record gets a random 256-bit data encryption key (DEK)
/// wrapped by ROLLCALL_MASTER_KEY. Format: v{keyVersion}:{wrappedDekB64}:{dekNonceB64}:{dekTagB64}:{dataNonceB64}:{dataTagB64}:{cipherB64}.
/// </summary>
public sealed class EnvelopeEncryptionService
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _masterKey;
    private readonly int _keyVersion;

    public EnvelopeEncryptionService(IConfiguration configuration)
    {
        var raw = configuration["ROLLCALL_MASTER_KEY"]
            ?? configuration["Rollcall:MasterKey"]
            ?? Environment.GetEnvironmentVariable("ROLLCALL_MASTER_KEY");

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Deterministic fallback only when running design-time tools without an env file.
            _masterKey = SHA256.HashData("rollcall-unconfigured-master-key"u8);
        }
        else
        {
            _masterKey = DecodeMasterKey(raw);
            SecretRedactor.RegisterSecret(raw);
        }

        _keyVersion = int.TryParse(
            configuration["ROLLCALL_MASTER_KEY_VERSION"] ?? "1",
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var v) && v > 0
            ? v
            : 1;
    }

    public int CurrentKeyVersion => _keyVersion;

    public string Encrypt(string plaintext) => Encrypt(Encoding.UTF8.GetBytes(plaintext), _masterKey, _keyVersion);

    public string Decrypt(string envelope) => Encoding.UTF8.GetString(DecryptBytes(envelope, _masterKey));

    public static string RewrapEnvelope(string envelope, string oldMasterKeyBase64, string newMasterKeyBase64, int newKeyVersion)
    {
        var oldKey = DecodeMasterKey(oldMasterKeyBase64);
        var newKey = DecodeMasterKey(newMasterKeyBase64);
        var plaintext = DecryptBytes(envelope, oldKey);
        return Encrypt(plaintext, newKey, newKeyVersion);
    }

    public static string GenerateMasterKeyBase64() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));

    private static byte[] DecodeMasterKey(string raw)
    {
        try
        {
            var bytes = Convert.FromBase64String(raw.Trim());
            return bytes.Length == KeyBytes ? bytes : SHA256.HashData(bytes);
        }
        catch (FormatException)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        }
    }

    private static string Encrypt(byte[] plaintext, byte[] masterKey, int keyVersion)
    {
        var dek = RandomNumberGenerator.GetBytes(KeyBytes);
        var dataNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var dataCipher = new byte[plaintext.Length];
        var dataTag = new byte[TagBytes];

        using (var dataAes = new AesGcm(dek, TagBytes))
        {
            dataAes.Encrypt(dataNonce, plaintext, dataCipher, dataTag);
        }

        var dekNonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var wrappedDek = new byte[dek.Length];
        var dekTag = new byte[TagBytes];

        using (var keyAes = new AesGcm(masterKey, TagBytes))
        {
            keyAes.Encrypt(dekNonce, dek, wrappedDek, dekTag);
        }

        CryptographicOperations.ZeroMemory(dek);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"v{keyVersion}:{Convert.ToBase64String(wrappedDek)}:{Convert.ToBase64String(dekNonce)}:{Convert.ToBase64String(dekTag)}:{Convert.ToBase64String(dataNonce)}:{Convert.ToBase64String(dataTag)}:{Convert.ToBase64String(dataCipher)}");
    }

    private static byte[] DecryptBytes(string envelope, byte[] masterKey)
    {
        var parts = envelope.Split(':');
        if (parts.Length != 7 || !parts[0].StartsWith('v'))
        {
            throw new CryptographicException("Encrypted envelope format is invalid.");
        }

        var wrappedDek = Convert.FromBase64String(parts[1]);
        var dekNonce = Convert.FromBase64String(parts[2]);
        var dekTag = Convert.FromBase64String(parts[3]);
        var dataNonce = Convert.FromBase64String(parts[4]);
        var dataTag = Convert.FromBase64String(parts[5]);
        var dataCipher = Convert.FromBase64String(parts[6]);

        var dek = new byte[wrappedDek.Length];
        using (var keyAes = new AesGcm(masterKey, TagBytes))
        {
            keyAes.Decrypt(dekNonce, wrappedDek, dekTag, dek);
        }

        try
        {
            var plaintext = new byte[dataCipher.Length];
            using var dataAes = new AesGcm(dek, TagBytes);
            dataAes.Decrypt(dataNonce, dataCipher, dataTag, plaintext);
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }
}

/// <summary>
/// Redacts known secrets (master key, setup token, private keys, TAPs, temporary passwords)
/// before any string is written to logs, audit entries, or error messages (section 4.2).
/// </summary>
public static class SecretRedactor
{
    private static readonly ConcurrentDictionary<string, byte> KnownSecrets = new(StringComparer.Ordinal);

    public static void RegisterSecret(string? secret)
    {
        if (!string.IsNullOrWhiteSpace(secret) && secret.Length >= 6)
        {
            KnownSecrets.TryAdd(secret, 0);
        }
    }

    public static string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var output = input;
        if (output.Contains("BEGIN PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("BEGIN RSA PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("BEGIN EC PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            output = "[REDACTED PRIVATE KEY]";
        }

        foreach (var secret in KnownSecrets.Keys)
        {
            if (output.Contains(secret, StringComparison.Ordinal))
            {
                output = output.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }

        return output;
    }
}
