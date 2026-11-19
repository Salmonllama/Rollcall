// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Infrastructure.Operations;

/// <summary>
/// Host CLI commands invoked by install.sh: admin-reset, rotate-master-key, and generate-permissions-doc.
/// </summary>
public sealed class OperationsCliCommands(
    RollcallDbContext db,
    AuditService auditService,
    TimeProvider clock)
{
    public async Task<int> AdminResetAsync(string upn, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            Console.Error.WriteLine("✕ Provide a user principal name, for example: admin-reset alex@contoso.com");
            return 2;
        }

        var normalized = upn.Trim();
        var user = await db.PortalUsers.FirstOrDefaultAsync(
            u => EF.Functions.ILike(u.Upn, normalized),
            cancellationToken);

        if (user is null)
        {
            user = new PortalUserEntity
            {
                Id = Guid.NewGuid(),
                EntraObjectId = string.Empty,
                Upn = normalized,
                DisplayName = normalized,
                CreatedAt = clock.GetUtcNow(),
            };
            user.SetRoles([PortalRole.Admin]);
            db.PortalUsers.Add(user);
        }
        else
        {
            var roles = user.GetRoles().Append(PortalRole.Admin).Distinct();
            user.SetRoles(roles);
            user.Disabled = false;
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditService.AppendAsync(
            "host-shell@rollcall",
            "Host administrator (install.sh admin-reset)",
            "portal.user.admin_reset",
            normalized,
            "succeeded",
            new { upn = normalized, roles = user.GetRoles() },
            summary: $"Granted Admin role to {normalized} via host shell.",
            cancellationToken: cancellationToken);

        Console.WriteLine($"✓ Granted Admin role to {normalized}.");
        return 0;
    }

    public async Task<int> RotateMasterKeyAsync(string oldMasterKey, string newMasterKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(oldMasterKey) || string.IsNullOrWhiteSpace(newMasterKey))
        {
            Console.Error.WriteLine("✕ Both ROLLCALL_OLD_MASTER_KEY and ROLLCALL_NEW_MASTER_KEY must be set.");
            return 2;
        }

        var certs = await db.AppCertificates.ToListAsync(cancellationToken);
        foreach (var cert in certs)
        {
            var nextVersion = cert.KeyVersion + 1;
            cert.EncryptedPrivateKey = EnvelopeEncryptionService.RewrapEnvelope(
                cert.EncryptedPrivateKey,
                oldMasterKey,
                newMasterKey,
                nextVersion);
            cert.KeyVersion = nextVersion;
        }

        await db.SaveChangesAsync(cancellationToken);
        await auditService.AppendAsync(
            "host-shell@rollcall",
            "Host administrator (install.sh rotate-master-key)",
            "security.master_key.rotated",
            "app_certificate",
            "succeeded",
            new { rewrappedCertificates = certs.Count },
            summary: $"Re-wrapped {certs.Count} encrypted record(s) with the new master key.",
            cancellationToken: cancellationToken);

        Console.WriteLine($"✓ Re-wrapped {certs.Count} encrypted certificate key(s) with the new master key.");
        return 0;
    }

    public static int GeneratePermissionsDoc(IEnumerable<IWorkflowStep> steps, string outputPath)
    {
        var allEnabled = FeatureFlagKeys.All.ToDictionary(k => k, _ => true, StringComparer.OrdinalIgnoreCase);
        var manifest = PermissionManifestBuilder.Build(steps, allEnabled);
        var markdown = PermissionManifestBuilder.RenderMarkdown(manifest);
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(outputPath, markdown);
        Console.WriteLine($"✓ Wrote {outputPath} ({manifest.Entries.Count} permissions).");
        return 0;
    }
}
