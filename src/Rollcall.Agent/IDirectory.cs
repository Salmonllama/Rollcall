// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Contracts;

namespace Rollcall.Agent;

public sealed record AdUserRecord(
    string DistinguishedName,
    string SamAccountName,
    string UserPrincipalName,
    string DisplayName,
    string? Department,
    string? Title,
    string? Office,
    string? Company,
    string? EmployeeId,
    string? Description,
    bool Enabled,
    IReadOnlyList<string> MemberOfDns);

public sealed record AdGroupRecord(
    string DistinguishedName,
    string Name,
    string SamAccountName,
    string Scope,
    string Category,
    string? Description,
    IReadOnlyList<string> MemberDns);

public interface IDirectory
{
    Task<AdUserRecord?> GetUserAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default);
    Task<AdGroupRecord?> GetGroupAsync(string groupDnOrSam, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdOuDto>> ListOusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdUserRecord>> SearchUsersAsync(string query, CancellationToken cancellationToken = default);
    Task<AdUserRecord> CreateUserAsync(AdUserCreatePayload payload, CancellationToken cancellationToken = default);
    Task<AdUserRecord> UpdateUserAsync(AdUserUpdatePayload payload, CancellationToken cancellationToken = default);
    Task<bool> SetUserEnabledAsync(string samAccountNameOrDn, bool enabled, CancellationToken cancellationToken = default);
    Task ResetPasswordAsync(string samAccountNameOrDn, string newPassword, bool mustChangeAtNextLogon, CancellationToken cancellationToken = default);
    Task<string> MoveUserAsync(string samAccountNameOrDn, string destinationOu, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> RemoveUserFromAllGroupsAsync(string samAccountNameOrDn, CancellationToken cancellationToken = default);
    Task<AdGroupRecord> CreateGroupAsync(AdGroupCreatePayload payload, CancellationToken cancellationToken = default);
    Task<AdGroupRecord> UpdateGroupAsync(AdGroupUpdatePayload payload, CancellationToken cancellationToken = default);
    Task<string> MoveGroupAsync(string groupDnOrSam, string destinationOu, CancellationToken cancellationToken = default);
    Task<bool> AddGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default);
    Task<bool> RemoveGroupMemberAsync(string groupDnOrSam, string memberDnOrSam, CancellationToken cancellationToken = default);
    Task TriggerEntraConnectDeltaSyncAsync(string? entraConnectServer, CancellationToken cancellationToken = default);
}

/// <summary>
/// Second line of defence inside the AD agent (section 4.7, M9 criterion 3): refuses any write
/// whose target or destination DN lies outside the configured allowed OUs, even if the portal
/// sends a forged job.
/// </summary>
public static class OuScopeGuard
{
    public static bool IsDnWithinAllowedOus(string dn, IReadOnlyList<string> allowedOus)
    {
        if (string.IsNullOrWhiteSpace(dn) || allowedOus.Count == 0)
        {
            return false;
        }

        var normalizedDn = NormalizeDn(dn);
        foreach (var allowed in allowedOus)
        {
            if (string.IsNullOrWhiteSpace(allowed))
            {
                continue;
            }

            var normalizedAllowed = NormalizeDn(allowed);
            if (string.Equals(normalizedDn, normalizedAllowed, StringComparison.OrdinalIgnoreCase) ||
                normalizedDn.EndsWith("," + normalizedAllowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void EnsureAllowed(string dn, IReadOnlyList<string> allowedOus, string operation)
    {
        if (!IsDnWithinAllowedOus(dn, allowedOus))
        {
            throw new UnauthorizedAccessException(
                $"AD agent refused {operation}: \"{dn}\" is outside the configured allowed OUs.");
        }
    }

    private static string NormalizeDn(string dn) =>
        string.Join(',', dn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
