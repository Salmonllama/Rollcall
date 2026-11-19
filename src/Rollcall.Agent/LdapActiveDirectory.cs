// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Rollcall.Contracts;

namespace Rollcall.Agent;

/// <summary>
/// Active Directory LDAP implementation using System.DirectoryServices.Protocols (authenticating via
/// the Windows gMSA Kerberos context — zero stored AD passwords) and PowerShell for Entra Connect delta sync.
/// </summary>
public sealed class LdapActiveDirectory(IConfiguration configuration) : IDirectory
{
    private string DomainController => configuration["Agent:DomainController"] ?? string.Empty;
    private string BaseDn => configuration["Agent:BaseDn"] ?? DeriveBaseDnFromDomain();

    public Task<AdUserRecord?> GetUserAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var filter = samAccountNameOrDn.Contains("CN=", StringComparison.OrdinalIgnoreCase)
            ? $"(&(objectClass=user)(distinguishedName={EscapeLdap(samAccountNameOrDn)}))"
            : $"(&(objectClass=user)(|(sAMAccountName={EscapeLdap(samAccountNameOrDn)})(userPrincipalName={EscapeLdap(samAccountNameOrDn)})))";
        var req = new SearchRequest(BaseDn, filter, SearchScope.Subtree,
            "distinguishedName", "sAMAccountName", "userPrincipalName", "displayName",
            "department", "title", "physicalDeliveryOfficeName", "company", "employeeID",
            "description", "userAccountControl", "memberOf");
        var resp = (SearchResponse)conn.SendRequest(req);
        if (resp.Entries.Count == 0)
        {
            return Task.FromResult<AdUserRecord?>(null);
        }

        return Task.FromResult<AdUserRecord?>(MapUser(resp.Entries[0]));
    }

    public Task<AdGroupRecord?> GetGroupAsync(string groupDnOrSam, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var filter = groupDnOrSam.Contains("CN=", StringComparison.OrdinalIgnoreCase)
            ? $"(&(objectClass=group)(distinguishedName={EscapeLdap(groupDnOrSam)}))"
            : $"(&(objectClass=group)(|(sAMAccountName={EscapeLdap(groupDnOrSam)})(cn={EscapeLdap(groupDnOrSam)})))";
        var req = new SearchRequest(BaseDn, filter, SearchScope.Subtree,
            "distinguishedName", "cn", "sAMAccountName", "groupType", "description", "member");
        var resp = (SearchResponse)conn.SendRequest(req);
        if (resp.Entries.Count == 0)
        {
            return Task.FromResult<AdGroupRecord?>(null);
        }

        return Task.FromResult<AdGroupRecord?>(MapGroup(resp.Entries[0]));
    }

    public Task<IReadOnlyList<AdOuDto>> ListOusAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var req = new SearchRequest(BaseDn, "(objectClass=organizationalUnit)", SearchScope.Subtree, "distinguishedName", "ou", "description");
        var resp = (SearchResponse)conn.SendRequest(req);
        var list = new List<AdOuDto>(resp.Entries.Count);
        foreach (SearchResultEntry entry in resp.Entries)
        {
            list.Add(new AdOuDto(
                entry.DistinguishedName,
                GetAttr(entry, "ou") ?? entry.DistinguishedName,
                GetAttr(entry, "description")));
        }

        return Task.FromResult<IReadOnlyList<AdOuDto>>(list);
    }

    public Task<IReadOnlyList<AdUserRecord>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        var escaped = EscapeLdap(query);
        var filter = string.IsNullOrWhiteSpace(query)
            ? "(&(objectClass=user)(objectCategory=person))"
            : $"(&(objectClass=user)(objectCategory=person)(|(displayName=*{escaped}*)(sAMAccountName=*{escaped}*)(userPrincipalName=*{escaped}*)))";
        var req = new SearchRequest(BaseDn, filter, SearchScope.Subtree,
            "distinguishedName", "sAMAccountName", "userPrincipalName", "displayName",
            "department", "title", "physicalDeliveryOfficeName", "company", "employeeID",
            "description", "userAccountControl", "memberOf");
        var resp = (SearchResponse)conn.SendRequest(req);
        var list = new List<AdUserRecord>(resp.Entries.Count);
        foreach (SearchResultEntry entry in resp.Entries)
        {
            list.Add(MapUser(entry));
        }

        return Task.FromResult<IReadOnlyList<AdUserRecord>>(list);
    }

    public async Task<AdUserRecord> CreateUserAsync(AdUserCreatePayload payload, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(payload.SamAccountName, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        using var conn = CreateConnection();
        var dn = $"CN={payload.DisplayName},{payload.TargetOu}";
        var attrs = new List<DirectoryAttribute>
        {
            new("objectClass", "user"),
            new("sAMAccountName", payload.SamAccountName),
            new("userPrincipalName", payload.UserPrincipalName),
            new("displayName", payload.DisplayName),
            new("givenName", payload.FirstName),
            new("sn", payload.LastName),
        };
        if (!string.IsNullOrWhiteSpace(payload.Department))
        {
            attrs.Add(new DirectoryAttribute("department", payload.Department));
        }

        if (!string.IsNullOrWhiteSpace(payload.Title))
        {
            attrs.Add(new DirectoryAttribute("title", payload.Title));
        }

        if (!string.IsNullOrWhiteSpace(payload.Office))
        {
            attrs.Add(new DirectoryAttribute("physicalDeliveryOfficeName", payload.Office));
        }

        if (!string.IsNullOrWhiteSpace(payload.Company))
        {
            attrs.Add(new DirectoryAttribute("company", payload.Company));
        }

        if (!string.IsNullOrWhiteSpace(payload.EmployeeId))
        {
            attrs.Add(new DirectoryAttribute("employeeID", payload.EmployeeId));
        }

        conn.SendRequest(new AddRequest(dn, [.. attrs]));

        if (!string.IsNullOrWhiteSpace(payload.InitialPassword))
        {
            await ResetPasswordAsync(dn, payload.InitialPassword, true, cancellationToken);
        }

        await SetUserEnabledAsync(dn, payload.Enabled, cancellationToken);
        return (await GetUserAsync(dn, cancellationToken))!;
    }

    public async Task<AdUserRecord> UpdateUserAsync(AdUserUpdatePayload payload, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(payload.SamAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException($"AD user \"{payload.SamAccountNameOrDn}\" was not found.");

        using var conn = CreateConnection();
        var mod = new ModifyRequest(existing.DistinguishedName);
        AddReplace(mod, "displayName", payload.DisplayName);
        AddReplace(mod, "department", payload.Department);
        AddReplace(mod, "title", payload.Title);
        AddReplace(mod, "physicalDeliveryOfficeName", payload.Office);
        AddReplace(mod, "telephoneNumber", payload.Phone);
        AddReplace(mod, "mobile", payload.Mobile);
        AddReplace(mod, "company", payload.Company);
        AddReplace(mod, "employeeID", payload.EmployeeId);
        AddReplace(mod, "manager", payload.ManagerDn);
        AddReplace(mod, "description", payload.Description);
        if (mod.Modifications.Count > 0)
        {
            conn.SendRequest(mod);
        }

        return (await GetUserAsync(existing.DistinguishedName, cancellationToken))!;
    }

    public async Task<bool> SetUserEnabledAsync(string samAccountNameOrDn, bool enabled, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(samAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException($"AD user \"{samAccountNameOrDn}\" was not found.");
        if (existing.Enabled == enabled)
        {
            return false;
        }

        using var conn = CreateConnection();
        var uac = enabled ? "512" : "514"; // NORMAL_ACCOUNT vs ACCOUNTDISABLE
        conn.SendRequest(new ModifyRequest(existing.DistinguishedName, DirectoryAttributeOperation.Replace, "userAccountControl", uac));
        return true;
    }

    public async Task ResetPasswordAsync(string samAccountNameOrDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(samAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException($"AD user \"{samAccountNameOrDn}\" was not found.");

        using var conn = CreateConnection();
        var pwdBytes = Encoding.Unicode.GetBytes($"\"{newPassword}\"");
        var mod = new ModifyRequest(existing.DistinguishedName);
        mod.Modifications.Add(new DirectoryAttributeModification { Operation = DirectoryAttributeOperation.Replace, Name = "unicodePwd" });
        mod.Modifications[0].Add(pwdBytes);
        if (mustChangeAtNextLogon)
        {
            var pwdLastSet = new DirectoryAttributeModification { Operation = DirectoryAttributeOperation.Replace, Name = "pwdLastSet" };
            pwdLastSet.Add("0");
            mod.Modifications.Add(pwdLastSet);
        }

        conn.SendRequest(mod);
    }

    public async Task<string> MoveUserAsync(string samAccountNameOrDn, string destinationOu, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(samAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException($"AD user \"{samAccountNameOrDn}\" was not found.");
        var rdn = existing.DistinguishedName.Split(',')[0];
        var newDn = $"{rdn},{destinationOu}";
        if (string.Equals(existing.DistinguishedName, newDn, StringComparison.OrdinalIgnoreCase))
        {
            return existing.DistinguishedName;
        }

        using var conn = CreateConnection();
        conn.SendRequest(new ModifyDNRequest(existing.DistinguishedName, destinationOu, rdn));
        return newDn;
    }

    public async Task<IReadOnlyList<string>> RemoveUserFromAllGroupsAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default)
    {
        var existing = await GetUserAsync(samAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException($"AD user \"{samAccountNameOrDn}\" was not found.");
        var removed = new List<string>();
        foreach (var groupDn in existing.MemberOfDns)
        {
            await RemoveGroupMemberAsync(groupDn, existing.DistinguishedName, cancellationToken);
            removed.Add(groupDn);
        }

        return removed;
    }

    public async Task<AdGroupRecord> CreateGroupAsync(AdGroupCreatePayload payload, CancellationToken cancellationToken = default)
    {
        var existing = await GetGroupAsync(payload.SamAccountName, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        using var conn = CreateConnection();
        var dn = $"CN={payload.Name},{payload.TargetOu}";
        var groupType = ComputeAdGroupType(payload.Scope, payload.Category);
        var attrs = new List<DirectoryAttribute>
        {
            new("objectClass", "group"),
            new("sAMAccountName", payload.SamAccountName),
            new("groupType", groupType.ToString(CultureInfo.InvariantCulture)),
        };
        if (!string.IsNullOrWhiteSpace(payload.Description))
        {
            attrs.Add(new DirectoryAttribute("description", payload.Description));
        }

        conn.SendRequest(new AddRequest(dn, [.. attrs]));
        return (await GetGroupAsync(dn, cancellationToken))!;
    }

    public async Task<AdGroupRecord> UpdateGroupAsync(AdGroupUpdatePayload payload, CancellationToken cancellationToken = default)
    {
        var existing = await GetGroupAsync(payload.GroupDnOrSam, cancellationToken)
            ?? throw new InvalidOperationException($"AD group \"{payload.GroupDnOrSam}\" was not found.");
        using var conn = CreateConnection();
        var mod = new ModifyRequest(existing.DistinguishedName);
        AddReplace(mod, "description", payload.Description);
        if (!string.IsNullOrWhiteSpace(payload.Scope) || !string.IsNullOrWhiteSpace(payload.Category))
        {
            var gt = ComputeAdGroupType(payload.Scope ?? existing.Scope, payload.Category ?? existing.Category);
            AddReplace(mod, "groupType", gt.ToString(CultureInfo.InvariantCulture));
        }

        if (mod.Modifications.Count > 0)
        {
            conn.SendRequest(mod);
        }

        var currentDn = existing.DistinguishedName;
        if (!string.IsNullOrWhiteSpace(payload.NewName) && !string.Equals(payload.NewName, existing.Name, StringComparison.Ordinal))
        {
            var parentOu = currentDn[(currentDn.IndexOf(',', StringComparison.Ordinal) + 1)..];
            conn.SendRequest(new ModifyDNRequest(currentDn, parentOu, $"CN={payload.NewName}"));
            currentDn = $"CN={payload.NewName},{parentOu}";
        }

        return (await GetGroupAsync(currentDn, cancellationToken))!;
    }

    public async Task<string> MoveGroupAsync(string groupDnOrSam, string destinationOu, CancellationToken cancellationToken = default)
    {
        var existing = await GetGroupAsync(groupDnOrSam, cancellationToken)
            ?? throw new InvalidOperationException($"AD group \"{groupDnOrSam}\" was not found.");
        var rdn = existing.DistinguishedName.Split(',')[0];
        var newDn = $"{rdn},{destinationOu}";
        if (string.Equals(existing.DistinguishedName, newDn, StringComparison.OrdinalIgnoreCase))
        {
            return existing.DistinguishedName;
        }

        using var conn = CreateConnection();
        conn.SendRequest(new ModifyDNRequest(existing.DistinguishedName, destinationOu, rdn));
        return newDn;
    }

    public async Task<bool> AddGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default)
    {
        var group = await GetGroupAsync(groupDnOrSam, cancellationToken)
            ?? throw new InvalidOperationException($"AD group \"{groupDnOrSam}\" was not found.");
        var memberDn = memberDnOrSam.Contains("CN=", StringComparison.OrdinalIgnoreCase)
            ? memberDnOrSam
            : (await GetUserAsync(memberDnOrSam, cancellationToken))?.DistinguishedName
                ?? throw new InvalidOperationException($"AD member \"{memberDnOrSam}\" was not found.");
        if (group.MemberDns.Contains(memberDn, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        using var conn = CreateConnection();
        conn.SendRequest(new ModifyRequest(group.DistinguishedName, DirectoryAttributeOperation.Add, "member", memberDn));
        return true;
    }

    public async Task<bool> RemoveGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default)
    {
        var group = await GetGroupAsync(groupDnOrSam, cancellationToken)
            ?? throw new InvalidOperationException($"AD group \"{groupDnOrSam}\" was not found.");
        var memberDn = memberDnOrSam.Contains("CN=", StringComparison.OrdinalIgnoreCase)
            ? memberDnOrSam
            : (await GetUserAsync(memberDnOrSam, cancellationToken))?.DistinguishedName ?? memberDnOrSam;
        if (!group.MemberDns.Contains(memberDn, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        using var conn = CreateConnection();
        conn.SendRequest(new ModifyRequest(group.DistinguishedName, DirectoryAttributeOperation.Delete, "member", memberDn));
        return true;
    }

    public async Task TriggerEntraConnectDeltaSyncAsync(string? entraConnectServer, CancellationToken cancellationToken = default)
    {
        var command = string.IsNullOrWhiteSpace(entraConnectServer)
            ? "Import-Module ADSync; Start-ADSyncSyncCycle -PolicyType Delta"
            : $"Invoke-Command -ComputerName '{entraConnectServer.Replace("'", "''", StringComparison.Ordinal)}' -ScriptBlock {{ Import-Module ADSync; Start-ADSyncSyncCycle -PolicyType Delta }}";

        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(command);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start PowerShell to trigger Entra Connect sync.");
        var stderr = await proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"Entra Connect delta sync failed: {stderr.Trim()}");
        }
    }

    private LdapConnection CreateConnection()
    {
        var identifier = string.IsNullOrWhiteSpace(DomainController)
            ? new LdapDirectoryIdentifier((string?)null!, 636, false, false)
            : new LdapDirectoryIdentifier(DomainController, 636);
        var conn = new LdapConnection(identifier)
        {
            AuthType = AuthType.Negotiate,
        };
        conn.SessionOptions.SecureSocketLayer = true;
        conn.SessionOptions.ProtocolVersion = 3;
        conn.Bind();
        return conn;
    }

    private static AdUserRecord MapUser(SearchResultEntry e)
    {
        var uac = int.TryParse(GetAttr(e, "userAccountControl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 512;
        var enabled = (uac & 0x0002) == 0;
        var memberOf = GetMultiAttr(e, "memberOf");
        return new AdUserRecord(
            e.DistinguishedName,
            GetAttr(e, "sAMAccountName") ?? string.Empty,
            GetAttr(e, "userPrincipalName") ?? string.Empty,
            GetAttr(e, "displayName") ?? string.Empty,
            GetAttr(e, "department"),
            GetAttr(e, "title"),
            GetAttr(e, "physicalDeliveryOfficeName"),
            GetAttr(e, "company"),
            GetAttr(e, "employeeID"),
            GetAttr(e, "description"),
            enabled,
            memberOf);
    }

    private static AdGroupRecord MapGroup(SearchResultEntry e)
    {
        var rawType = int.TryParse(GetAttr(e, "groupType"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var gt) ? gt : -2147483646;
        var category = (rawType & unchecked((int)0x80000000)) != 0 ? "Security" : "Distribution";
        var scope = (rawType & 0x8) != 0 ? "Universal" : (rawType & 0x4) != 0 ? "DomainLocal" : "Global";
        return new AdGroupRecord(
            e.DistinguishedName,
            GetAttr(e, "cn") ?? string.Empty,
            GetAttr(e, "sAMAccountName") ?? string.Empty,
            scope,
            category,
            GetAttr(e, "description"),
            GetMultiAttr(e, "member"));
    }

    private static int ComputeAdGroupType(string scope, string category)
    {
        var val = scope.ToLowerInvariant() switch
        {
            "universal" => 0x00000008,
            "domainlocal" => 0x00000004,
            _ => 0x00000002,
        };
        if (string.Equals(category, "Security", StringComparison.OrdinalIgnoreCase))
        {
            val |= unchecked((int)0x80000000);
        }

        return val;
    }

    private static void AddReplace(ModifyRequest mod, string attr, string? val)
    {
        if (val is null)
        {
            return;
        }

        var item = new DirectoryAttributeModification { Operation = DirectoryAttributeOperation.Replace, Name = attr };
        if (val.Length > 0)
        {
            item.Add(val);
        }

        mod.Modifications.Add(item);
    }

    private static string? GetAttr(SearchResultEntry e, string name) =>
        e.Attributes.Contains(name) && e.Attributes[name].Count > 0 ? e.Attributes[name][0]?.ToString() : null;

    private static List<string> GetMultiAttr(SearchResultEntry e, string name)
    {
        var list = new List<string>();
        if (e.Attributes.Contains(name))
        {
            foreach (var v in e.Attributes[name])
            {
                if (v?.ToString() is { Length: > 0 } s)
                {
                    list.Add(s);
                }
            }
        }

        return list;
    }

    private static string EscapeLdap(string value) =>
        value.Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);

    private static string DeriveBaseDnFromDomain()
    {
        var domain = Environment.GetEnvironmentVariable("USERDNSDOMAIN") ?? "corp.local";
        return string.Join(',', domain.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => $"DC={p}"));
    }
}
