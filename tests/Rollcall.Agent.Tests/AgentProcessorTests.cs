// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Rollcall.Agent;
using Rollcall.Contracts;

namespace Rollcall.Agent.Tests;

public sealed class InMemoryDirectory : IDirectory
{
    public Dictionary<string, AdUserRecord> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AdGroupRecord> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AdOuDto> Ous { get; } =
    [
        new("OU=Staff,DC=contoso,DC=local", "Staff", "Standard employees"),
        new("OU=Offboarding,DC=contoso,DC=local", "Offboarding", "Disabled leavers"),
        new("OU=Groups,DC=contoso,DC=local", "Groups", "Managed groups"),
        new("OU=DomainAdmins,DC=contoso,DC=local", "DomainAdmins", "Restricted OU"),
    ];
    public int DeltaSyncCount { get; private set; }

    public Task<AdUserRecord?> GetUserAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default) =>
        Task.FromResult(Users.Values.FirstOrDefault(u =>
            string.Equals(u.SamAccountName, samAccountNameOrDn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.DistinguishedName, samAccountNameOrDn, StringComparison.OrdinalIgnoreCase)));

    public Task<AdGroupRecord?> GetGroupAsync(string groupDnOrSam, CancellationToken cancellationToken = default) =>
        Task.FromResult(Groups.Values.FirstOrDefault(g =>
            string.Equals(g.SamAccountName, groupDnOrSam, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(g.DistinguishedName, groupDnOrSam, StringComparison.OrdinalIgnoreCase)));

    public Task<IReadOnlyList<AdOuDto>> ListOusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdOuDto>>(Ous);

    public Task<IReadOnlyList<AdUserRecord>> SearchUsersAsync(string query, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AdUserRecord>>(Users.Values
            .Where(u => u.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                u.SamAccountName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());

    public Task<AdUserRecord> CreateUserAsync(AdUserCreatePayload payload, CancellationToken cancellationToken = default)
    {
        if (Users.TryGetValue(payload.SamAccountName, out var existing))
        {
            return Task.FromResult(existing);
        }

        var user = new AdUserRecord(
            $"CN={payload.DisplayName},{payload.TargetOu}",
            payload.SamAccountName,
            payload.UserPrincipalName,
            payload.DisplayName,
            payload.Department,
            payload.Title,
            payload.Office,
            payload.Company,
            payload.EmployeeId,
            null,
            payload.Enabled,
            []);
        Users[user.SamAccountName] = user;
        return Task.FromResult(user);
    }

    public async Task<AdUserRecord> UpdateUserAsync(AdUserUpdatePayload payload, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(payload.SamAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        var updated = user with
        {
            DisplayName = payload.DisplayName ?? user.DisplayName,
            Department = payload.Department ?? user.Department,
            Title = payload.Title ?? user.Title,
            Office = payload.Office ?? user.Office,
            Company = payload.Company ?? user.Company,
            EmployeeId = payload.EmployeeId ?? user.EmployeeId,
            Description = payload.Description ?? user.Description,
        };
        Users[user.SamAccountName] = updated;
        return updated;
    }

    public async Task<bool> SetUserEnabledAsync(string samAccountNameOrDn, bool enabled, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(samAccountNameOrDn, cancellationToken)
            ?? throw new InvalidOperationException("User not found.");
        if (user.Enabled == enabled)
        {
            return false;
        }

        Users[user.SamAccountName] = user with { Enabled = enabled };
        return true;
    }

    public async Task ResetPasswordAsync(string samAccountNameOrDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken cancellationToken = default)
    {
        _ = await GetUserAsync(samAccountNameOrDn, cancellationToken) ?? throw new InvalidOperationException("User not found.");
    }

    public async Task<string> MoveUserAsync(string samAccountNameOrDn, string destinationOu, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(samAccountNameOrDn, cancellationToken) ?? throw new InvalidOperationException("User not found.");
        var rdn = user.DistinguishedName.Split(',')[0];
        var newDn = $"{rdn},{destinationOu}";
        Users[user.SamAccountName] = user with { DistinguishedName = newDn };
        return newDn;
    }

    public async Task<IReadOnlyList<string>> RemoveUserFromAllGroupsAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default)
    {
        var user = await GetUserAsync(samAccountNameOrDn, cancellationToken) ?? throw new InvalidOperationException("User not found.");
        var removed = user.MemberOfDns.ToList();
        Users[user.SamAccountName] = user with { MemberOfDns = [] };
        foreach (var grp in Groups.Values.ToList())
        {
            Groups[grp.SamAccountName] = grp with
            {
                MemberDns = grp.MemberDns.Where(m => !string.Equals(m, user.DistinguishedName, StringComparison.OrdinalIgnoreCase)).ToList(),
            };
        }

        return removed;
    }

    public Task<AdGroupRecord> CreateGroupAsync(AdGroupCreatePayload payload, CancellationToken cancellationToken = default)
    {
        if (Groups.TryGetValue(payload.SamAccountName, out var existing))
        {
            return Task.FromResult(existing);
        }

        var grp = new AdGroupRecord($"CN={payload.Name},{payload.TargetOu}", payload.Name, payload.SamAccountName, payload.Scope, payload.Category, payload.Description, []);
        Groups[grp.SamAccountName] = grp;
        return Task.FromResult(grp);
    }

    public async Task<AdGroupRecord> UpdateGroupAsync(AdGroupUpdatePayload payload, CancellationToken cancellationToken = default)
    {
        var grp = await GetGroupAsync(payload.GroupDnOrSam, cancellationToken) ?? throw new InvalidOperationException("Group not found.");
        var updated = grp with
        {
            Name = payload.NewName ?? grp.Name,
            Description = payload.Description ?? grp.Description,
            Scope = payload.Scope ?? grp.Scope,
            Category = payload.Category ?? grp.Category,
        };
        Groups[grp.SamAccountName] = updated;
        return updated;
    }

    public async Task<string> MoveGroupAsync(string groupDnOrSam, string destinationOu, CancellationToken cancellationToken = default)
    {
        var grp = await GetGroupAsync(groupDnOrSam, cancellationToken) ?? throw new InvalidOperationException("Group not found.");
        var rdn = grp.DistinguishedName.Split(',')[0];
        var newDn = $"{rdn},{destinationOu}";
        Groups[grp.SamAccountName] = grp with { DistinguishedName = newDn };
        return newDn;
    }

    public async Task<bool> AddGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default)
    {
        var grp = await GetGroupAsync(groupDnOrSam, cancellationToken) ?? throw new InvalidOperationException("Group not found.");
        var user = await GetUserAsync(memberDnOrSam, cancellationToken);
        var memberDn = user?.DistinguishedName ?? memberDnOrSam;
        if (grp.MemberDns.Contains(memberDn, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        Groups[grp.SamAccountName] = grp with { MemberDns = [.. grp.MemberDns, memberDn] };
        if (user is not null)
        {
            Users[user.SamAccountName] = user with { MemberOfDns = [.. user.MemberOfDns, grp.DistinguishedName] };
        }

        return true;
    }

    public async Task<bool> RemoveGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default)
    {
        var grp = await GetGroupAsync(groupDnOrSam, cancellationToken) ?? throw new InvalidOperationException("Group not found.");
        var user = await GetUserAsync(memberDnOrSam, cancellationToken);
        var memberDn = user?.DistinguishedName ?? memberDnOrSam;
        if (!grp.MemberDns.Contains(memberDn, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        Groups[grp.SamAccountName] = grp with
        {
            MemberDns = grp.MemberDns.Where(m => !string.Equals(m, memberDn, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        return true;
    }

    public Task TriggerEntraConnectDeltaSyncAsync(string? entraConnectServer, CancellationToken cancellationToken = default)
    {
        DeltaSyncCount++;
        return Task.CompletedTask;
    }
}

public sealed class AgentProcessorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllowedOus =
    [
        "OU=Staff,DC=contoso,DC=local",
        "OU=Offboarding,DC=contoso,DC=local",
        "OU=Groups,DC=contoso,DC=local",
    ];

    [Fact]
    public async Task Agent_refuses_forged_write_outside_allowed_ous()
    {
        var dir = new InMemoryDirectory();
        var processor = new AgentJobProcessor(dir);

        var forgedPayload = JsonSerializer.Serialize(new AdUserCreatePayload(
            "eviladmin", "evil@contoso.com", "Evil Admin", "Evil", "Admin",
            "OU=DomainAdmins,DC=contoso,DC=local", null, null, null, null, null, null, true, "Pass123!"), JsonOptions);

        var res = await processor.ExecuteAsync(AdJobKinds.UserCreate, forgedPayload, AllowedOus);

        Assert.False(res.Succeeded);
        Assert.Contains("outside the configured allowed OUs", res.PlainLanguageError, StringComparison.Ordinal);
        Assert.Empty(dir.Users);
    }

    [Fact]
    public async Task Agent_handles_all_ad_job_types_with_success_noop_and_failure()
    {
        var dir = new InMemoryDirectory();
        var processor = new AgentJobProcessor(dir);

        // Create user
        var create = await processor.ExecuteAsync(
            AdJobKinds.UserCreate,
            JsonSerializer.Serialize(new AdUserCreatePayload("jdoe", "jane.doe@contoso.com", "Jane Doe", "Jane", "Doe",
                "OU=Staff,DC=contoso,DC=local", "Sales", "Associate", "Seattle", "Contoso", "E100", null, true, "Start!123"), JsonOptions),
            AllowedOus);
        Assert.True(create.Succeeded);

        // Update user
        var update = await processor.ExecuteAsync(
            AdJobKinds.UserUpdate,
            JsonSerializer.Serialize(new AdUserUpdatePayload("jdoe", null, null, "Finance", "Senior Associate", null, null, null, null, null, null, null), JsonOptions),
            AllowedOus);
        Assert.True(update.Succeeded);
        Assert.Equal("Finance", dir.Users["jdoe"].Department);

        // Create group, update, move, add/remove member (including no-op re-add)
        var grpCreate = await processor.ExecuteAsync(
            AdJobKinds.GroupCreate,
            JsonSerializer.Serialize(new AdGroupCreatePayload("Finance Staff", "sg-fin", "OU=Groups,DC=contoso,DC=local", "Global", "Security", "Finance team"), JsonOptions),
            AllowedOus);
        Assert.True(grpCreate.Succeeded);

        var grpUpdate = await processor.ExecuteAsync(
            AdJobKinds.GroupUpdate,
            JsonSerializer.Serialize(new AdGroupUpdatePayload("sg-fin", "Finance Core", "Updated", "Universal", "Security"), JsonOptions),
            AllowedOus);
        Assert.True(grpUpdate.Succeeded);

        var addMember = await processor.ExecuteAsync(
            AdJobKinds.GroupAddMember,
            JsonSerializer.Serialize(new AdGroupMembershipPayload("sg-fin", "jdoe"), JsonOptions),
            AllowedOus);
        Assert.True(addMember.Succeeded);
        Assert.Contains("\"changed\":true", addMember.ResultJson, StringComparison.Ordinal);

        var addMemberNoOp = await processor.ExecuteAsync(
            AdJobKinds.GroupAddMember,
            JsonSerializer.Serialize(new AdGroupMembershipPayload("sg-fin", "jdoe"), JsonOptions),
            AllowedOus);
        Assert.True(addMemberNoOp.Succeeded);
        Assert.Contains("\"changed\":false", addMemberNoOp.ResultJson, StringComparison.Ordinal);

        // Remove all groups, reset password, disable, move to Offboarding OU, sync delta
        var removeAll = await processor.ExecuteAsync(
            AdJobKinds.UserRemoveFromAllGroups,
            JsonSerializer.Serialize(new AdUserRemoveFromAllGroupsPayload("jdoe"), JsonOptions),
            AllowedOus);
        Assert.True(removeAll.Succeeded);

        var pwd = await processor.ExecuteAsync(
            AdJobKinds.UserResetPassword,
            JsonSerializer.Serialize(new AdUserResetPasswordPayload("jdoe", "NewPass!99", true), JsonOptions),
            AllowedOus);
        Assert.True(pwd.Succeeded);

        var disable = await processor.ExecuteAsync(
            AdJobKinds.UserSetEnabled,
            JsonSerializer.Serialize(new AdUserSetEnabledPayload("jdoe", false), JsonOptions),
            AllowedOus);
        Assert.True(disable.Succeeded);
        Assert.False(dir.Users["jdoe"].Enabled);

        var move = await processor.ExecuteAsync(
            AdJobKinds.UserMove,
            JsonSerializer.Serialize(new AdUserMovePayload("jdoe", "OU=Offboarding,DC=contoso,DC=local"), JsonOptions),
            AllowedOus);
        Assert.True(move.Succeeded);
        Assert.EndsWith("OU=Offboarding,DC=contoso,DC=local", dir.Users["jdoe"].DistinguishedName, StringComparison.Ordinal);

        var sync = await processor.ExecuteAsync(AdJobKinds.SyncDelta, JsonSerializer.Serialize(new AdSyncDeltaPayload("SYNC-01"), JsonOptions), AllowedOus);
        Assert.True(sync.Succeeded);
        Assert.Equal(1, dir.DeltaSyncCount);

        // Read queries
        Assert.True((await processor.ExecuteAsync(AdJobKinds.OuList, "{}", AllowedOus)).Succeeded);
        Assert.True((await processor.ExecuteAsync(AdJobKinds.UserGet, "{\"identity\":\"jdoe\"}", AllowedOus)).Succeeded);
        Assert.True((await processor.ExecuteAsync(AdJobKinds.GroupGet, "{\"identity\":\"sg-fin\"}", AllowedOus)).Succeeded);
        Assert.True((await processor.ExecuteAsync(AdJobKinds.Search, "{\"query\":\"Jane\"}", AllowedOus)).Succeeded);

        // Failure on missing user
        var missing = await processor.ExecuteAsync(
            AdJobKinds.UserSetEnabled,
            JsonSerializer.Serialize(new AdUserSetEnabledPayload("nobody", false), JsonOptions),
            AllowedOus);
        Assert.False(missing.Succeeded);
    }

    [Fact]
    public void Agent_contract_json_shapes_are_pinned()
    {
        var lease = new AgentJobLeaseItemDto(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            null,
            null,
            AdJobKinds.UserCreate,
            "{}",
            DateTimeOffset.Parse("2026-11-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            AllowedOus);
        var json = JsonSerializer.Serialize(lease, JsonOptions);
        Assert.Contains("\"jobId\":\"11111111-1111-1111-1111-111111111111\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"ad.user.create\"", json, StringComparison.Ordinal);
        Assert.Contains("\"allowedOus\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_key_store_generates_p256_public_key_and_signed_jwt()
    {
        using var ks = AgentKeyStore.CreateNew();
        Assert.Contains("BEGIN PUBLIC KEY", ks.PublicKeyPem, StringComparison.Ordinal);
        var jwt = ks.CreateSignedJwt(Guid.NewGuid());
        Assert.Equal(3, jwt.Split('.').Length);
    }
}
