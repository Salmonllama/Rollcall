// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;

namespace Rollcall.Infrastructure.Security;

/// <summary>
/// Generates RSA 3072 self-signed X.509 certificates (per ADR 0002), encrypts private keys at rest,
/// and loads X509Certificate2 instances purely in memory using EphemeralKeySet.
/// </summary>
public sealed class AppCertificateService(EnvelopeEncryptionService encryption, TimeProvider clock)
{
    public const int CertificateValidityDays = 365;
    public const int ExpiryWarningDays = 30;

    public AppCertificateEntity GenerateCertificate(string hostname)
    {
        var subject = $"CN=Rollcall {hostname}";
        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var notBefore = clock.GetUtcNow().AddMinutes(-5);
        var notAfter = notBefore.AddDays(CertificateValidityDays);
        using var cert = request.CreateSelfSigned(notBefore, notAfter);

        var publicDer = cert.Export(X509ContentType.Cert);
        var privateKeyPkcs8 = rsa.ExportPkcs8PrivateKey();
        var privateKeyBase64 = Convert.ToBase64String(privateKeyPkcs8);
        SecretRedactor.RegisterSecret(privateKeyBase64);
        var encryptedPrivateKey = encryption.Encrypt(privateKeyBase64);
        CryptographicOperations.ZeroMemory(privateKeyPkcs8);

        return new AppCertificateEntity
        {
            Id = Guid.NewGuid(),
            Thumbprint = cert.Thumbprint,
            Subject = subject,
            Algorithm = "RSA-3072",
            NotBefore = notBefore,
            NotAfter = notAfter,
            PublicCertificateDer = publicDer,
            EncryptedPrivateKey = encryptedPrivateKey,
            KeyVersion = encryption.CurrentKeyVersion,
            Status = AppCertificateStatus.Pending,
            CreatedAt = clock.GetUtcNow(),
        };
    }

    /// <summary>Loads an X509Certificate2 with its RSA private key in memory only (no file written to disk).</summary>
    public X509Certificate2 LoadInMemory(AppCertificateEntity entity)
    {
        var privateKeyBase64 = encryption.Decrypt(entity.EncryptedPrivateKey);
        var privateKeyBytes = Convert.FromBase64String(privateKeyBase64);
        try
        {
            using var publicCert = X509CertificateLoader.LoadCertificate(entity.PublicCertificateDer);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKeyBytes, out _);
            using var withKey = publicCert.CopyWithPrivateKey(rsa);
            var pfx = withKey.Export(X509ContentType.Pfx);
            try
            {
                return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfx);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKeyBytes);
        }
    }

    public AppCertificateSummaryDto ToSummary(AppCertificateEntity entity)
    {
        var days = (int)Math.Floor((entity.NotAfter - clock.GetUtcNow()).TotalDays);
        return new AppCertificateSummaryDto(
            entity.Id,
            entity.Thumbprint,
            entity.Subject,
            entity.Algorithm,
            entity.NotBefore,
            entity.NotAfter,
            entity.Status,
            days);
    }

    public bool ShouldWarnExpiry(AppCertificateEntity? activeCert) =>
        activeCert is not null && (activeCert.NotAfter - clock.GetUtcNow()).TotalDays <= ExpiryWarningDays;

    public static string ExportPublicPem(byte[] publicDer) =>
        PemEncoding.WriteString("CERTIFICATE", publicDer);
}

/// <summary>
/// Generates the guided Microsoft Graph PowerShell script from the live permission manifest (M2 / M10).
/// </summary>
public static class AppRegistrationScriptGenerator
{
    public static string GenerateSetupScript(
        string hostname,
        string redirectUri,
        byte[] publicCertDer,
        PermissionManifestDto manifest)
    {
        var certBase64 = Convert.ToBase64String(publicCertDer);
        var enabledEntries = manifest.Entries.Where(e => e.IsEnabled).ToList();
        var graphPerms = enabledEntries
            .Where(e => e.Api == "Microsoft Graph")
            .Select(e => e.Permission)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var exchangePerms = enabledEntries
            .Where(e => e.Api == "Office 365 Exchange Online")
            .Select(e => e.Permission)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var directoryRoles = enabledEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.DirectoryRole))
            .Select(e => e.DirectoryRole!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine("# Rollcall Entra ID app registration script (certificate-only, no client secret)");
        sb.AppendLine("# Run in Azure Cloud Shell or PowerShell 7 as a Global Administrator.");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Connect-MgGraph -Scopes 'Application.ReadWrite.All','AppRoleAssignment.ReadWrite.All','RoleManagement.ReadWrite.Directory','Directory.Read.All'");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"$displayName = 'Rollcall ({hostname})'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$redirectUri = '{redirectUri}'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$certBytes = [Convert]::FromBase64String('{certBase64}')");
        sb.AppendLine();
        sb.AppendLine("$app = New-MgApplication -DisplayName $displayName -SignInAudience 'AzureADMyOrg' -Web @{ RedirectUris = @($redirectUri) } -KeyCredentials @(@{ Type = 'AsymmetricX509Cert'; Usage = 'Verify'; Key = $certBytes })");
        sb.AppendLine("$sp = New-MgServicePrincipal -AppId $app.AppId");
        sb.AppendLine();
        sb.AppendLine("# Grant Microsoft Graph application permissions");
        sb.AppendLine("$graphSp = Get-MgServicePrincipal -Filter \"appId eq '00000003-0000-0000-c000-000000000000'\"");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$graphPermNames = @({string.Join(", ", graphPerms.Select(p => $"'{p}'"))})");
        sb.AppendLine("foreach ($permName in $graphPermNames) {");
        sb.AppendLine("  $role = $graphSp.AppRoles | Where-Object { $_.Value -eq $permName } | Select-Object -First 1");
        sb.AppendLine("  if ($role) { New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $graphSp.Id -AppRoleId $role.Id | Out-Null }");
        sb.AppendLine("}");

        if (exchangePerms.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Grant Office 365 Exchange Online application permissions");
            sb.AppendLine("$exoSp = Get-MgServicePrincipal -Filter \"appId eq '00000002-0000-0ff1-ce00-000000000000'\"");
            sb.AppendLine(CultureInfo.InvariantCulture, $"$exoPermNames = @({string.Join(", ", exchangePerms.Select(p => $"'{p}'"))})");
            sb.AppendLine("foreach ($permName in $exoPermNames) {");
            sb.AppendLine("  $role = $exoSp.AppRoles | Where-Object { $_.Value -eq $permName } | Select-Object -First 1");
            sb.AppendLine("  if ($role) { New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $sp.Id -PrincipalId $sp.Id -ResourceId $exoSp.Id -AppRoleId $role.Id | Out-Null }");
            sb.AppendLine("}");
        }

        if (directoryRoles.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Assign required Entra ID directory roles to the service principal");
            sb.AppendLine(CultureInfo.InvariantCulture, $"$dirRoles = @({string.Join(", ", directoryRoles.Select(r => $"'{r}'"))})");
            sb.AppendLine("foreach ($roleName in $dirRoles) {");
            sb.AppendLine("  $def = Get-MgRoleManagementDirectoryRoleDefinition -Filter \"displayName eq '$roleName'\" | Select-Object -First 1");
            sb.AppendLine("  if ($def) { New-MgRoleManagementDirectoryRoleAssignment -PrincipalId $sp.Id -RoleDefinitionId $def.Id -DirectoryScopeId '/' | Out-Null }");
            sb.AppendLine("}");
        }

        sb.AppendLine();
        sb.AppendLine("$org = Get-MgOrganization | Select-Object -First 1");
        sb.AppendLine("$domain = ($org.VerifiedDomains | Where-Object { $_.IsDefault } | Select-Object -First 1).Name");
        sb.AppendLine("Write-Host \"Tenant ID:      $($org.Id)\"");
        sb.AppendLine("Write-Host \"Client ID:      $($app.AppId)\"");
        sb.AppendLine("Write-Host \"Primary domain: $domain\"");
        return sb.ToString();
    }

    public static string GenerateRotationScript(string clientId, byte[] newPublicCertDer)
    {
        var certBase64 = Convert.ToBase64String(newPublicCertDer);
        var sb = new StringBuilder();
        sb.AppendLine("# Rollcall certificate rotation script: adds the new certificate alongside the current one.");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("Connect-MgGraph -Scopes 'Application.ReadWrite.All'");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$app = Get-MgApplication -Filter \"appId eq '{clientId}'\" | Select-Object -First 1");
        sb.AppendLine(CultureInfo.InvariantCulture, $"$newCert = [Convert]::FromBase64String('{certBase64}')");
        sb.AppendLine("$keys = @($app.KeyCredentials) + @(@{ Type = 'AsymmetricX509Cert'; Usage = 'Verify'; Key = $newCert })");
        sb.AppendLine("Update-MgApplication -ApplicationId $app.Id -KeyCredentials $keys");
        sb.AppendLine("Write-Host 'New certificate uploaded alongside the existing certificate. Return to Rollcall to validate and activate it.'");
        return sb.ToString();
    }
}
