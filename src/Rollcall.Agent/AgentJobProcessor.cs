// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Rollcall.Contracts;

namespace Rollcall.Agent;

/// <summary>
/// Executes all 16 AD agent job types against IDirectory while strictly enforcing OuScopeGuard
/// on every write operation (section 4.7, M9).
/// </summary>
public sealed class AgentJobProcessor(IDirectory directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AgentJobResultRequest> ExecuteAsync(
        string jobKind,
        string payloadJson,
        IReadOnlyList<string> allowedOus,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await DispatchAsync(jobKind, payloadJson, allowedOus, cancellationToken);
            return new AgentJobResultRequest(
                Succeeded: true,
                ResultJson: JsonSerializer.Serialize(result, JsonOptions),
                PlainLanguageError: null,
                RawError: null,
                IsTransient: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new AgentJobResultRequest(false, null, ex.Message, ex.Message, false);
        }
        catch (Exception ex)
        {
            return new AgentJobResultRequest(
                Succeeded: false,
                ResultJson: null,
                PlainLanguageError: $"Active Directory step \"{jobKind}\" failed: {ex.Message}",
                RawError: ex.ToString(),
                IsTransient: false);
        }
    }

    private async Task<object> DispatchAsync(
        string jobKind,
        string payloadJson,
        IReadOnlyList<string> allowedOus,
        CancellationToken cancellationToken)
    {
        switch (jobKind)
        {
            case AdJobKinds.UserCreate:
                {
                    var p = Deserialize<AdUserCreatePayload>(payloadJson);
                    OuScopeGuard.EnsureAllowed(p.TargetOu, allowedOus, jobKind);
                    return await directory.CreateUserAsync(p, cancellationToken);
                }
            case AdJobKinds.UserUpdate:
                {
                    var p = Deserialize<AdUserUpdatePayload>(payloadJson);
                    var existing = await directory.GetUserAsync(p.SamAccountNameOrDn, cancellationToken)
                        ?? throw new InvalidOperationException($"AD user \"{p.SamAccountNameOrDn}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    return await directory.UpdateUserAsync(p, cancellationToken);
                }
            case AdJobKinds.UserSetEnabled:
                {
                    var p = Deserialize<AdUserSetEnabledPayload>(payloadJson);
                    var existing = await directory.GetUserAsync(p.SamAccountNameOrDn, cancellationToken)
                        ?? throw new InvalidOperationException($"AD user \"{p.SamAccountNameOrDn}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    var changed = await directory.SetUserEnabledAsync(p.SamAccountNameOrDn, p.Enabled, cancellationToken);
                    return new { existing.SamAccountName, p.Enabled, changed };
                }
            case AdJobKinds.UserResetPassword:
                {
                    var p = Deserialize<AdUserResetPasswordPayload>(payloadJson);
                    var existing = await directory.GetUserAsync(p.SamAccountNameOrDn, cancellationToken)
                        ?? throw new InvalidOperationException($"AD user \"{p.SamAccountNameOrDn}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    await directory.ResetPasswordAsync(p.SamAccountNameOrDn, p.NewPassword, p.MustChangeAtNextLogon, cancellationToken);
                    return new { existing.SamAccountName, reset = true };
                }
            case AdJobKinds.UserMove:
                {
                    var p = Deserialize<AdUserMovePayload>(payloadJson);
                    var existing = await directory.GetUserAsync(p.SamAccountNameOrDn, cancellationToken)
                        ?? throw new InvalidOperationException($"AD user \"{p.SamAccountNameOrDn}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    OuScopeGuard.EnsureAllowed(p.DestinationOu, allowedOus, jobKind);
                    var newDn = await directory.MoveUserAsync(p.SamAccountNameOrDn, p.DestinationOu, cancellationToken);
                    return new { existing.SamAccountName, newDn };
                }
            case AdJobKinds.UserRemoveFromAllGroups:
                {
                    var p = Deserialize<AdUserRemoveFromAllGroupsPayload>(payloadJson);
                    var existing = await directory.GetUserAsync(p.SamAccountNameOrDn, cancellationToken)
                        ?? throw new InvalidOperationException($"AD user \"{p.SamAccountNameOrDn}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    var removed = await directory.RemoveUserFromAllGroupsAsync(p.SamAccountNameOrDn, cancellationToken);
                    return new { existing.SamAccountName, removedGroups = removed };
                }
            case AdJobKinds.GroupCreate:
                {
                    var p = Deserialize<AdGroupCreatePayload>(payloadJson);
                    OuScopeGuard.EnsureAllowed(p.TargetOu, allowedOus, jobKind);
                    return await directory.CreateGroupAsync(p, cancellationToken);
                }
            case AdJobKinds.GroupUpdate:
                {
                    var p = Deserialize<AdGroupUpdatePayload>(payloadJson);
                    var existing = await directory.GetGroupAsync(p.GroupDnOrSam, cancellationToken)
                        ?? throw new InvalidOperationException($"AD group \"{p.GroupDnOrSam}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    return await directory.UpdateGroupAsync(p, cancellationToken);
                }
            case AdJobKinds.GroupMove:
                {
                    var p = Deserialize<AdGroupMovePayload>(payloadJson);
                    var existing = await directory.GetGroupAsync(p.GroupDnOrSam, cancellationToken)
                        ?? throw new InvalidOperationException($"AD group \"{p.GroupDnOrSam}\" was not found.");
                    OuScopeGuard.EnsureAllowed(existing.DistinguishedName, allowedOus, jobKind);
                    OuScopeGuard.EnsureAllowed(p.DestinationOu, allowedOus, jobKind);
                    var newDn = await directory.MoveGroupAsync(p.GroupDnOrSam, p.DestinationOu, cancellationToken);
                    return new { existing.SamAccountName, newDn };
                }
            case AdJobKinds.GroupAddMember:
                {
                    var p = Deserialize<AdGroupMembershipPayload>(payloadJson);
                    var group = await directory.GetGroupAsync(p.GroupDnOrSam, cancellationToken)
                        ?? throw new InvalidOperationException($"AD group \"{p.GroupDnOrSam}\" was not found.");
                    OuScopeGuard.EnsureAllowed(group.DistinguishedName, allowedOus, jobKind);
                    var changed = await directory.AddGroupMemberAsync(p.GroupDnOrSam, p.MemberDnOrSam, cancellationToken);
                    return new { group.DistinguishedName, p.MemberDnOrSam, changed };
                }
            case AdJobKinds.GroupRemoveMember:
                {
                    var p = Deserialize<AdGroupMembershipPayload>(payloadJson);
                    var group = await directory.GetGroupAsync(p.GroupDnOrSam, cancellationToken)
                        ?? throw new InvalidOperationException($"AD group \"{p.GroupDnOrSam}\" was not found.");
                    OuScopeGuard.EnsureAllowed(group.DistinguishedName, allowedOus, jobKind);
                    var changed = await directory.RemoveGroupMemberAsync(p.GroupDnOrSam, p.MemberDnOrSam, cancellationToken);
                    return new { group.DistinguishedName, p.MemberDnOrSam, changed };
                }
            case AdJobKinds.OuList:
                return await directory.ListOusAsync(cancellationToken);
            case AdJobKinds.UserGet:
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    var id = doc.RootElement.GetProperty("identity").GetString() ?? string.Empty;
                    return (object?)await directory.GetUserAsync(id, cancellationToken) ?? new { found = false };
                }
            case AdJobKinds.GroupGet:
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    var id = doc.RootElement.GetProperty("identity").GetString() ?? string.Empty;
                    return (object?)await directory.GetGroupAsync(id, cancellationToken) ?? new { found = false };
                }
            case AdJobKinds.Search:
                {
                    using var doc = JsonDocument.Parse(payloadJson);
                    var q = doc.RootElement.TryGetProperty("query", out var qEl) ? qEl.GetString() ?? string.Empty : string.Empty;
                    return await directory.SearchUsersAsync(q, cancellationToken);
                }
            case AdJobKinds.SyncDelta:
                {
                    var p = string.IsNullOrWhiteSpace(payloadJson) ? new AdSyncDeltaPayload(null) : Deserialize<AdSyncDeltaPayload>(payloadJson);
                    await directory.TriggerEntraConnectDeltaSyncAsync(p.EntraConnectServer, cancellationToken);
                    return new { synced = true, server = p.EntraConnectServer ?? Environment.MachineName };
                }
            default:
                throw new NotSupportedException($"Unsupported AD agent job kind \"{jobKind}\".");
        }
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Couldn't deserialize payload for {typeof(T).Name}.");
}
