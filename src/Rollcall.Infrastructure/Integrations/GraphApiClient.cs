// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Rollcall.Contracts;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;

namespace Rollcall.Infrastructure.Integrations;

/// <summary>
/// Microsoft Graph v1.0 HTTP client authenticating exclusively with an RSA 3072 X.509 client assertion
/// (RFC 7523). Uses $select on every read, $batch for bulk operations, and honours Retry-After.
/// </summary>
public sealed class GraphApiClient(
    IHttpClientFactory httpClientFactory,
    RollcallDbContext db,
    AppCertificateService certService,
    IConfiguration configuration,
    TimeProvider clock) : IGraphClient
{
    private const string UserSelect =
        "id,userPrincipalName,displayName,mail,jobTitle,department,officeLocation,employeeId,usageLocation,companyName,businessPhones,mobilePhone,accountEnabled,onPremisesSyncEnabled,onPremisesSamAccountName,onPremisesDistinguishedName,userType,assignedLicenses,createdDateTime";

    private const string GroupSelect =
        "id,displayName,mail,description,groupTypes,securityEnabled,mailEnabled,onPremisesSyncEnabled,onPremisesDistinguishedName";

    private const string SkuSelect = "skuId,skuPartNumber,prepaidUnits,consumedUnits,servicePlans";
    private const string PolicySelect = "id,displayName,state,conditions,grantControls,modifiedDateTime";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string GraphBaseUrl =>
        (configuration["Graph:BaseUrl"] ?? "https://graph.microsoft.com/v1.0").TrimEnd('/');

    private string TokenBaseUrl =>
        (configuration["Graph:TokenBaseUrl"] ?? "https://login.microsoftonline.com").TrimEnd('/');

    public async Task<GraphOrganizationInfo> ValidateConnectionAsync(
        string tenantId,
        string clientId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken = default)
    {
        var token = await AcquireTokenAsync(tenantId, clientId, certificate, cancellationToken);
        var grantedRoles = ExtractRolesFromToken(token);

        using var orgDoc = await SendJsonAsync(
            HttpMethod.Get,
            "/organization?$select=id,displayName,verifiedDomains",
            token,
            body: null,
            extraHeaders: null,
            actionDescription: "read organization details",
            cancellationToken);

        var org = orgDoc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        var displayName = org.ValueKind == JsonValueKind.Object && org.TryGetProperty("displayName", out var dn)
            ? dn.GetString() ?? tenantId
            : tenantId;
        var primaryDomain = tenantId;
        if (org.ValueKind == JsonValueKind.Object && org.TryGetProperty("verifiedDomains", out var domains))
        {
            foreach (var d in domains.EnumerateArray())
            {
                if (d.TryGetProperty("isDefault", out var isDef) && isDef.GetBoolean() &&
                    d.TryGetProperty("name", out var nameEl) && nameEl.GetString() is { } name)
                {
                    primaryDomain = name;
                    break;
                }
            }
        }

        var assignedDirectoryRoles = await FetchServicePrincipalDirectoryRolesAsync(clientId, token, cancellationToken);
        return new GraphOrganizationInfo(tenantId, displayName, primaryDomain, grantedRoles, assignedDirectoryRoles);
    }

    public async Task<IReadOnlyList<DirectoryUserDto>> SearchUsersAsync(
        string? query,
        string? department,
        bool? accountEnabled,
        bool? onPremisesSyncEnabled,
        bool? licensed,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConsistencyLevel"] = "eventual",
        };

        var qs = new List<string>
        {
            $"$select={UserSelect}",
            $"$top={Math.Clamp(limit, 1, 500)}",
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            var escaped = Uri.EscapeDataString(query.Trim());
            qs.Add($"$search={escaped}");
        }

        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(department))
        {
            filters.Add($"department eq '{department.Replace("'", "''", StringComparison.Ordinal)}'");
        }

        if (accountEnabled.HasValue)
        {
            filters.Add($"accountEnabled eq {(accountEnabled.Value ? "true" : "false")}");
        }

        if (onPremisesSyncEnabled.HasValue)
        {
            filters.Add($"onPremisesSyncEnabled eq {(onPremisesSyncEnabled.Value ? "true" : "false")}");
        }

        if (filters.Count > 0)
        {
            qs.Add($"$filter={Uri.EscapeDataString(string.Join(" and ", filters))}");
        }

        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users?{string.Join('&', qs)}",
            token,
            body: null,
            extraHeaders: headers,
            actionDescription: "search users",
            cancellationToken);

        var users = doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(ParseUser)
            .ToList();

        if (licensed.HasValue)
        {
            users = users.Where(u => licensed.Value ? u.LicenseCount > 0 : u.LicenseCount == 0).ToList();
        }

        return users;
    }

    public async Task<DirectoryUserDto?> GetUserAsync(string userIdOrUpn, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        try
        {
            using var doc = await SendJsonAsync(
                HttpMethod.Get,
                $"/users/{Uri.EscapeDataString(userIdOrUpn)}?$select={UserSelect}",
                token,
                null,
                null,
                $"read user {userIdOrUpn}",
                cancellationToken);
            return ParseUser(doc.RootElement);
        }
        catch (StepExecutionException ex) when (ex.RawError.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<DirectoryUserDto?> GetUserBySamAccountNameAsync(string samAccountName, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var filter = Uri.EscapeDataString($"onPremisesSamAccountName eq '{samAccountName.Replace("'", "''", StringComparison.Ordinal)}'");
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users?$select={UserSelect}&$filter={filter}",
            token,
            null,
            new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" },
            $"look up synced user {samAccountName}",
            cancellationToken);

        var first = doc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
        return first.ValueKind == JsonValueKind.Object ? ParseUser(first) : null;
    }

    public async Task<DirectoryUserDto?> GetManagerAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        try
        {
            using var doc = await SendJsonAsync(
                HttpMethod.Get,
                $"/users/{Uri.EscapeDataString(userId)}/manager?$select={UserSelect}",
                token,
                null,
                null,
                $"read manager for {userId}",
                cancellationToken);
            return ParseUser(doc.RootElement);
        }
        catch (StepExecutionException ex) when (ex.RawError.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<DirectoryGroupDto>> GetUserGroupsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}/memberOf?$select={GroupSelect}",
            token,
            null,
            null,
            $"read groups for {userId}",
            cancellationToken);

        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Where(e => !e.TryGetProperty("@odata.type", out var t) ||
                string.Equals(t.GetString(), "#microsoft.graph.group", StringComparison.OrdinalIgnoreCase))
            .Select(ParseGroup)
            .ToList();
    }

    public async Task<IReadOnlyList<UserLicenseAssignmentDto>> GetUserLicensesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}?$select=id,assignedLicenses,licenseAssignmentStates",
            token,
            null,
            null,
            $"read licenses for {userId}",
            cancellationToken);

        var skus = (await ListSubscribedSkusAsync(cancellationToken)).ToDictionary(s => s.SkuId, StringComparer.OrdinalIgnoreCase);
        var groupsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<UserLicenseAssignmentDto>();

        if (doc.RootElement.TryGetProperty("licenseAssignmentStates", out var states) && states.ValueKind == JsonValueKind.Array && states.GetArrayLength() > 0)
        {
            foreach (var state in states.EnumerateArray())
            {
                var skuId = state.GetProperty("skuId").GetString() ?? string.Empty;
                var assignedByGroup = state.TryGetProperty("assignedByGroup", out var bg) && bg.ValueKind == JsonValueKind.String
                    ? bg.GetString()
                    : null;
                string? sourceGroupName = null;
                if (!string.IsNullOrWhiteSpace(assignedByGroup))
                {
                    if (!groupsById.TryGetValue(assignedByGroup, out sourceGroupName))
                    {
                        var detail = await GetGroupDetailAsync(assignedByGroup, cancellationToken);
                        sourceGroupName = detail?.Group.DisplayName ?? assignedByGroup;
                        groupsById[assignedByGroup] = sourceGroupName;
                    }
                }

                var disabledPlans = state.TryGetProperty("disabledPlans", out var dp) && dp.ValueKind == JsonValueKind.Array
                    ? dp.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                    : [];

                skus.TryGetValue(skuId, out var sku);
                results.Add(new UserLicenseAssignmentDto(
                    skuId,
                    sku?.SkuPartNumber ?? skuId,
                    sku?.FriendlyName ?? FriendlySkuNames.Resolve(sku?.SkuPartNumber ?? skuId),
                    !string.IsNullOrWhiteSpace(assignedByGroup),
                    assignedByGroup,
                    sourceGroupName,
                    disabledPlans));
            }

            return results;
        }

        if (doc.RootElement.TryGetProperty("assignedLicenses", out var assigned) && assigned.ValueKind == JsonValueKind.Array)
        {
            foreach (var lic in assigned.EnumerateArray())
            {
                var skuId = lic.GetProperty("skuId").GetString() ?? string.Empty;
                var disabledPlans = lic.TryGetProperty("disabledPlans", out var dp) && dp.ValueKind == JsonValueKind.Array
                    ? dp.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
                    : [];
                skus.TryGetValue(skuId, out var sku);
                results.Add(new UserLicenseAssignmentDto(
                    skuId,
                    sku?.SkuPartNumber ?? skuId,
                    sku?.FriendlyName ?? FriendlySkuNames.Resolve(sku?.SkuPartNumber ?? skuId),
                    false,
                    null,
                    null,
                    disabledPlans));
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<MfaMethodDto>> GetUserMfaMethodsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}/authentication/methods?$select=id,displayName,phoneNumber,phoneType,createdDateTime",
            token,
            null,
            null,
            $"read MFA methods for {userId}",
            cancellationToken);

        var methods = new List<MfaMethodDto>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString() ?? string.Empty;
            var odataType = item.TryGetProperty("@odata.type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var methodType = odataType.Replace("#microsoft.graph.", string.Empty, StringComparison.OrdinalIgnoreCase);
            var displayName = item.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? methodType
                : methodType;
            var detail = item.TryGetProperty("phoneNumber", out var pn) && pn.ValueKind == JsonValueKind.String
                ? pn.GetString()
                : null;
            DateTimeOffset? created = item.TryGetProperty("createdDateTime", out var ct) && ct.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ct.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
            var removable = !string.Equals(methodType, "passwordAuthenticationMethod", StringComparison.OrdinalIgnoreCase);

            methods.Add(new MfaMethodDto(id, methodType, displayName, detail, created, removable));
        }

        return methods;
    }

    public async Task<IReadOnlyList<string>> GetUserDirectoryRolesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}/memberOf?$select=id,displayName",
            token,
            null,
            null,
            $"read directory roles for {userId}",
            cancellationToken);

        var roles = new List<string>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            if (item.TryGetProperty("@odata.type", out var t) &&
                string.Equals(t.GetString(), "#microsoft.graph.directoryRole", StringComparison.OrdinalIgnoreCase) &&
                item.TryGetProperty("displayName", out var dn) && dn.GetString() is { } roleName)
            {
                roles.Add(roleName);
            }
        }

        return roles;
    }

    public async Task<IReadOnlyList<string>> GetUserOwnedGroupNamesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}/ownedObjects?$select=id,displayName",
            token,
            null,
            null,
            $"read owned groups for {userId}",
            cancellationToken);

        return doc.RootElement.GetProperty("value")
            .EnumerateArray()
            .Select(e => e.TryGetProperty("displayName", out var dn) ? dn.GetString() : null)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetUserCustomAttributesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/users/{Uri.EscapeDataString(userId)}?$select=id,onPremisesExtensionAttributes",
            token,
            null,
            null,
            $"read custom attributes for {userId}",
            cancellationToken);

        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (doc.RootElement.TryGetProperty("onPremisesExtensionAttributes", out var ext) && ext.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in ext.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                {
                    dict[prop.Name] = prop.Value.GetString()!;
                }
            }
        }

        return dict;
    }

    public async Task<DirectoryUserDto> CreateUserAsync(GraphCreateUserRequest request, CancellationToken cancellationToken = default)
    {
        SecretRedactor.RegisterSecret(request.Password);
        var token = await GetActiveTokenAsync(cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["accountEnabled"] = request.AccountEnabled,
            ["displayName"] = request.DisplayName,
            ["mailNickname"] = request.MailNickname,
            ["userPrincipalName"] = request.UserPrincipalName,
            ["givenName"] = request.GivenName,
            ["surname"] = request.Surname,
            ["passwordProfile"] = new
            {
                forceChangePasswordNextSignIn = request.ForceChangePasswordNextSignIn,
                password = request.Password,
            },
        };
        if (!string.IsNullOrWhiteSpace(request.Department))
        {
            payload["department"] = request.Department;
        }

        if (!string.IsNullOrWhiteSpace(request.JobTitle))
        {
            payload["jobTitle"] = request.JobTitle;
        }

        if (!string.IsNullOrWhiteSpace(request.OfficeLocation))
        {
            payload["officeLocation"] = request.OfficeLocation;
        }

        if (!string.IsNullOrWhiteSpace(request.CompanyName))
        {
            payload["companyName"] = request.CompanyName;
        }

        if (!string.IsNullOrWhiteSpace(request.UsageLocation))
        {
            payload["usageLocation"] = request.UsageLocation;
        }

        using var doc = await SendJsonAsync(
            HttpMethod.Post,
            "/users",
            token,
            payload,
            null,
            $"create user {request.UserPrincipalName}",
            cancellationToken);
        return ParseUser(doc.RootElement);
    }

    public async Task UpdateUserAttributesAsync(string userId, IReadOnlyDictionary<string, object?> patchFields, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Patch,
            $"/users/{Uri.EscapeDataString(userId)}",
            token,
            patchFields,
            $"update attributes for {userId}",
            cancellationToken);
    }

    public async Task SetManagerAsync(string userId, string? managerId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(managerId))
        {
            await SendNoContentAsync(
                HttpMethod.Delete,
                $"/users/{Uri.EscapeDataString(userId)}/manager/$ref",
                token,
                null,
                $"clear manager for {userId}",
                cancellationToken);
            return;
        }

        await SendNoContentAsync(
            HttpMethod.Put,
            $"/users/{Uri.EscapeDataString(userId)}/manager/$ref",
            token,
            new Dictionary<string, string> { ["@odata.id"] = $"{GraphBaseUrl}/users/{managerId}" },
            $"set manager for {userId}",
            cancellationToken);
    }

    public async Task RevokeSignInSessionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var _ = await SendJsonAsync(
            HttpMethod.Post,
            $"/users/{Uri.EscapeDataString(userId)}/revokeSignInSessions",
            token,
            new { },
            null,
            $"revoke sessions for {userId}",
            cancellationToken);
    }

    public async Task ResetPasswordAsync(string userId, string newPassword, bool forceChangeNextSignIn, CancellationToken cancellationToken = default)
    {
        SecretRedactor.RegisterSecret(newPassword);
        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Patch,
            $"/users/{Uri.EscapeDataString(userId)}",
            token,
            new
            {
                passwordProfile = new
                {
                    password = newPassword,
                    forceChangePasswordNextSignIn = forceChangeNextSignIn,
                },
            },
            $"reset password for {userId}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<DirectoryGroupDto>> ListGroupsAsync(string? query, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var qs = new List<string> { $"$select={GroupSelect}", "$top=200" };
        Dictionary<string, string>? headers = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            qs.Add($"$search={Uri.EscapeDataString(query.Trim())}");
            headers = new Dictionary<string, string> { ["ConsistencyLevel"] = "eventual" };
        }

        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/groups?{string.Join('&', qs)}",
            token,
            null,
            headers,
            "list groups",
            cancellationToken);

        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseGroup).ToList();
    }

    public async Task<DirectoryGroupDetailDto?> GetGroupDetailAsync(string groupId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        try
        {
            using var groupDoc = await SendJsonAsync(
                HttpMethod.Get,
                $"/groups/{Uri.EscapeDataString(groupId)}?$select={GroupSelect}",
                token,
                null,
                null,
                $"read group {groupId}",
                cancellationToken);
            using var membersDoc = await SendJsonAsync(
                HttpMethod.Get,
                $"/groups/{Uri.EscapeDataString(groupId)}/members?$select={UserSelect}",
                token,
                null,
                null,
                $"read members of {groupId}",
                cancellationToken);
            using var ownersDoc = await SendJsonAsync(
                HttpMethod.Get,
                $"/groups/{Uri.EscapeDataString(groupId)}/owners?$select={UserSelect}",
                token,
                null,
                null,
                $"read owners of {groupId}",
                cancellationToken);

            var members = membersDoc.RootElement.GetProperty("value").EnumerateArray().Select(ParseUser).ToList();
            var owners = ownersDoc.RootElement.GetProperty("value").EnumerateArray().Select(ParseUser).ToList();
            var group = ParseGroup(groupDoc.RootElement) with { MemberCount = members.Count };
            return new DirectoryGroupDetailDto(group, members, owners);
        }
        catch (StepExecutionException ex) when (ex.RawError.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task AddGroupMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Post,
            $"/groups/{Uri.EscapeDataString(groupId)}/members/$ref",
            token,
            new Dictionary<string, string> { ["@odata.id"] = $"{GraphBaseUrl}/directoryObjects/{userId}" },
            $"add user {userId} to group {groupId}",
            cancellationToken);
    }

    public async Task RemoveGroupMemberAsync(string groupId, string userId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Delete,
            $"/groups/{Uri.EscapeDataString(groupId)}/members/{Uri.EscapeDataString(userId)}/$ref",
            token,
            null,
            $"remove user {userId} from group {groupId}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<GraphBatchItemResult>> ExecuteBatchAsync(
        IReadOnlyList<GraphBatchOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var allResults = new List<GraphBatchItemResult>();

        // Graph $batch supports up to 20 sub-requests per call (50 users -> 3 HTTP calls).
        foreach (var chunk in operations.Chunk(20))
        {
            var payload = new
            {
                requests = chunk.Select(op => new
                {
                    id = op.Id,
                    method = op.Method,
                    url = op.Url,
                    headers = op.Body is not null
                        ? new Dictionary<string, string> { ["Content-Type"] = "application/json" }
                        : null,
                    body = op.Body,
                }),
            };

            using var doc = await SendJsonAsync(
                HttpMethod.Post,
                "/$batch",
                token,
                payload,
                null,
                $"execute Graph batch ({chunk.Length} operations)",
                cancellationToken);

            foreach (var resp in doc.RootElement.GetProperty("responses").EnumerateArray())
            {
                var id = resp.GetProperty("id").GetString() ?? string.Empty;
                var status = resp.GetProperty("status").GetInt32();
                string? errorMsg = null;
                if (status >= 400 && resp.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object &&
                    b.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
                {
                    errorMsg = msg.GetString();
                }

                allResults.Add(new GraphBatchItemResult(id, status, errorMsg));
            }
        }

        return allResults;
    }

    public async Task<IReadOnlyList<LicenseSkuDto>> ListSubscribedSkusAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/subscribedSkus?$select={SkuSelect}",
            token,
            null,
            null,
            "list subscribed license SKUs",
            cancellationToken);

        var skus = new List<LicenseSkuDto>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            var skuId = item.GetProperty("skuId").GetString() ?? string.Empty;
            var partNumber = item.GetProperty("skuPartNumber").GetString() ?? skuId;
            var enabled = item.TryGetProperty("prepaidUnits", out var pu) && pu.TryGetProperty("enabled", out var en)
                ? en.GetInt32()
                : 0;
            var consumed = item.TryGetProperty("consumedUnits", out var cu) ? cu.GetInt32() : 0;
            var plans = item.TryGetProperty("servicePlans", out var sp) && sp.ValueKind == JsonValueKind.Array
                ? sp.EnumerateArray().Select(p => new ServicePlanDto(
                    p.GetProperty("servicePlanId").GetString() ?? string.Empty,
                    p.GetProperty("servicePlanName").GetString() ?? string.Empty,
                    p.TryGetProperty("appliesTo", out var at) ? at.GetString() ?? "User" : "User")).ToList()
                : [];

            skus.Add(new LicenseSkuDto(
                skuId,
                partNumber,
                FriendlySkuNames.Resolve(partNumber),
                enabled,
                consumed,
                Math.Max(0, enabled - consumed),
                plans));
        }

        return skus;
    }

    public async Task AssignLicensesAsync(
        string userId,
        IReadOnlyList<TemplateLicenseSpec> addLicenses,
        IReadOnlyList<string> removeSkuIds,
        CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var payload = new
        {
            addLicenses = addLicenses.Select(l => new
            {
                skuId = l.SkuId,
                disabledPlans = l.DisabledPlans,
            }),
            removeLicenses = removeSkuIds,
        };

        using var _ = await SendJsonAsync(
            HttpMethod.Post,
            $"/users/{Uri.EscapeDataString(userId)}/assignLicense",
            token,
            payload,
            null,
            $"update licenses for {userId}",
            cancellationToken);
    }

    public async Task RemoveMfaMethodAsync(string userId, string methodType, string methodId, CancellationToken cancellationToken = default)
    {
        var segment = methodType switch
        {
            "phoneAuthenticationMethod" => "phoneMethods",
            "microsoftAuthenticatorAuthenticationMethod" => "microsoftAuthenticatorMethods",
            "fido2AuthenticationMethod" => "fido2Methods",
            "softwareOathAuthenticationMethod" => "softwareOathMethods",
            "windowsHelloForBusinessAuthenticationMethod" => "windowsHelloForBusinessMethods",
            "temporaryAccessPassAuthenticationMethod" => "temporaryAccessPassMethods",
            _ => "methods",
        };

        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Delete,
            $"/users/{Uri.EscapeDataString(userId)}/authentication/{segment}/{Uri.EscapeDataString(methodId)}",
            token,
            null,
            $"remove MFA method {methodType} for {userId}",
            cancellationToken);
    }

    public async Task<GraphTapResult> IssueTemporaryAccessPassAsync(string userId, int lifetimeMinutes, bool isUsableOnce, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Post,
            $"/users/{Uri.EscapeDataString(userId)}/authentication/temporaryAccessPassMethods",
            token,
            new
            {
                lifetimeInMinutes = lifetimeMinutes,
                isUsableOnce,
            },
            null,
            $"issue a Temporary Access Pass for {userId}",
            cancellationToken);

        var root = doc.RootElement;
        var tap = root.GetProperty("temporaryAccessPass").GetString() ?? string.Empty;
        SecretRedactor.RegisterSecret(tap);
        var id = root.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("D");
        var start = root.TryGetProperty("startDateTime", out var st) && st.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(st.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : clock.GetUtcNow();
        return new GraphTapResult(id, tap, start, lifetimeMinutes, isUsableOnce);
    }

    public async Task<IReadOnlyList<ConditionalAccessPolicyDto>> ListConditionalAccessPoliciesAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Get,
            $"/identity/conditionalAccess/policies?$select={PolicySelect}",
            token,
            null,
            null,
            "list Conditional Access policies",
            cancellationToken);

        return doc.RootElement.GetProperty("value").EnumerateArray().Select(ParseCaPolicy).ToList();
    }

    public async Task<ConditionalAccessPolicyDto?> GetConditionalAccessPolicyAsync(string policyId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        try
        {
            using var doc = await SendJsonAsync(
                HttpMethod.Get,
                $"/identity/conditionalAccess/policies/{Uri.EscapeDataString(policyId)}?$select={PolicySelect}",
                token,
                null,
                null,
                $"read Conditional Access policy {policyId}",
                cancellationToken);
            return ParseCaPolicy(doc.RootElement);
        }
        catch (StepExecutionException ex) when (ex.RawError.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary>PATCHes ONLY conditions.users on a Conditional Access policy (M8 criterion 2).</summary>
    public async Task PatchConditionalAccessUsersAsync(
        string policyId,
        IReadOnlyList<string> includeUsers,
        IReadOnlyList<string> excludeUsers,
        IReadOnlyList<string> includeGroups,
        IReadOnlyList<string> excludeGroups,
        CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        var payload = new
        {
            conditions = new
            {
                users = new
                {
                    includeUsers,
                    excludeUsers,
                    includeGroups,
                    excludeGroups,
                },
            },
        };

        await SendNoContentAsync(
            HttpMethod.Patch,
            $"/identity/conditionalAccess/policies/{Uri.EscapeDataString(policyId)}",
            token,
            payload,
            $"update Conditional Access policy {policyId}",
            cancellationToken);
    }

    private static readonly string[] ReadRoles = ["read"];

    public async Task<GraphOneDriveGrant> GrantOneDriveAccessAsync(string userId, string managerUpn, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        using var doc = await SendJsonAsync(
            HttpMethod.Post,
            $"/users/{Uri.EscapeDataString(userId)}/drive/root/invite",
            token,
            new
            {
                requireSignIn = true,
                sendInvitation = false,
                roles = ReadRoles,
                recipients = new[] { new { email = managerUpn } },
            },
            null,
            $"grant OneDrive access on {userId} to {managerUpn}",
            cancellationToken);

        var first = doc.RootElement.TryGetProperty("value", out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().FirstOrDefault()
            : doc.RootElement;
        var permId = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var idEl)
            ? idEl.GetString() ?? Guid.NewGuid().ToString("D")
            : Guid.NewGuid().ToString("D");

        return new GraphOneDriveGrant(permId, managerUpn, "read");
    }

    public async Task RevokeOneDriveAccessAsync(string userId, string permissionId, CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        await SendNoContentAsync(
            HttpMethod.Delete,
            $"/users/{Uri.EscapeDataString(userId)}/drive/root/permissions/{Uri.EscapeDataString(permissionId)}",
            token,
            null,
            $"remove OneDrive access {permissionId} on {userId}",
            cancellationToken);
    }

    public async Task<int?> GetOneDriveRetentionDaysAsync(CancellationToken cancellationToken = default)
    {
        var token = await GetActiveTokenAsync(cancellationToken);
        try
        {
            using var doc = await SendJsonAsync(
                HttpMethod.Get,
                "/admin/sharepoint/settings?$select=deletedUserPersonalSiteRetentionPeriodInDays",
                token,
                null,
                null,
                "read OneDrive retention setting",
                cancellationToken);
            if (doc.RootElement.TryGetProperty("deletedUserPersonalSiteRetentionPeriodInDays", out var days) && days.ValueKind == JsonValueKind.Number)
            {
                return days.GetInt32();
            }

            return null;
        }
        catch (StepExecutionException)
        {
            return null;
        }
    }

    private async Task<string> GetActiveTokenAsync(CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new StepExecutionException("Rollcall isn't connected to a Microsoft 365 tenant yet.", "Tenant record missing.");

        var activeCert = await db.AppCertificates.AsNoTracking()
            .Where(c => c.Status == AppCertificateStatus.Active)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new StepExecutionException("No active app certificate is configured.", "Active AppCertificate missing.");

        using var cert = certService.LoadInMemory(activeCert);
        return await AcquireTokenAsync(tenant.TenantId, tenant.AppClientId, cert, cancellationToken);
    }

    private async Task<string> AcquireTokenAsync(
        string tenantId,
        string clientId,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        var tokenEndpoint = $"{TokenBaseUrl}/{tenantId}/oauth2/v2.0/token";
        var assertion = CreateClientAssertionJwt(clientId, tokenEndpoint, certificate);

        var client = httpClientFactory.CreateClient("RollcallGraph");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
        });

        using var response = await client.PostAsync(new Uri(tokenEndpoint), content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new StepExecutionException(
                "Couldn't authenticate to Microsoft Entra ID with the app certificate. Confirm the certificate is uploaded to the app registration and the tenant ID and client ID match.",
                $"HTTP {(int)response.StatusCode}: {SecretRedactor.Redact(body)}",
                isTransient: (int)response.StatusCode is 429 or >= 500);
        }

        using var doc = JsonDocument.Parse(body);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new StepExecutionException("Entra ID returned an empty access token.", "Missing access_token.");
        return accessToken;
    }

    private string CreateClientAssertionJwt(string clientId, string audience, X509Certificate2 certificate)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var key = new X509SecurityKey(certificate);
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);

        var header = new JwtHeader(credentials);
        header["x5t"] = Base64UrlEncoder.Encode(certificate.GetCertHash(HashAlgorithmName.SHA1));

        var payload = new JwtPayload(
            issuer: clientId,
            audience: audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, clientId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5));

        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static List<string> ExtractRolesFromToken(string jwt)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(jwt))
            {
                return [];
            }

            var parsed = handler.ReadJwtToken(jwt);
            return parsed.Claims
                .Where(c => string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (ArgumentException)
        {
            return [];
        }
    }

    private async Task<List<string>> FetchServicePrincipalDirectoryRolesAsync(
        string clientId,
        string token,
        CancellationToken cancellationToken)
    {
        try
        {
            var filter = Uri.EscapeDataString($"appId eq '{clientId}'");
            using var spDoc = await SendJsonAsync(
                HttpMethod.Get,
                $"/servicePrincipals?$select=id,appId&$filter={filter}",
                token,
                null,
                null,
                "find Rollcall service principal",
                cancellationToken);

            var sp = spDoc.RootElement.GetProperty("value").EnumerateArray().FirstOrDefault();
            if (sp.ValueKind != JsonValueKind.Object || !sp.TryGetProperty("id", out var spIdEl) || spIdEl.GetString() is not { } spId)
            {
                return [];
            }

            using var rolesDoc = await SendJsonAsync(
                HttpMethod.Get,
                $"/servicePrincipals/{Uri.EscapeDataString(spId)}/memberOf?$select=id,displayName",
                token,
                null,
                null,
                "read service principal directory roles",
                cancellationToken);

            return rolesDoc.RootElement.GetProperty("value")
                .EnumerateArray()
                .Where(e => e.TryGetProperty("@odata.type", out var t) &&
                    string.Equals(t.GetString(), "#microsoft.graph.directoryRole", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.TryGetProperty("displayName", out var dn) ? dn.GetString() : null)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (StepExecutionException)
        {
            return [];
        }
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativePath,
        string bearerToken,
        object? body,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, relativePath, bearerToken, body, extraHeaders, actionDescription, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(text) ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
    }

    private async Task SendNoContentAsync(
        HttpMethod method,
        string relativePath,
        string bearerToken,
        object? body,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        using var _ = await SendCoreAsync(method, relativePath, bearerToken, body, null, actionDescription, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string relativePath,
        string bearerToken,
        object? body,
        IReadOnlyDictionary<string, string>? extraHeaders,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("RollcallGraph");
        var clientRequestId = Guid.NewGuid().ToString("D");
        var url = $"{GraphBaseUrl}{relativePath}";

        using var request = new HttpRequestMessage(method, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.TryAddWithoutValidation("client-request-id", clientRequestId);
        if (extraHeaders is not null)
        {
            foreach (var kv in extraHeaders)
            {
                request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
        }

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new StepExecutionException(
                PlainLanguageErrorMapper.MapGraphError(actionDescription, 503, ex.Message),
                $"Network error calling {method} {relativePath}: {ex.Message}",
                isTransient: true,
                graphClientRequestId: clientRequestId,
                innerException: ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        var statusCode = (int)response.StatusCode;
        var rawBody = SecretRedactor.Redact(await response.Content.ReadAsStringAsync(cancellationToken));
        var graphRequestId = response.Headers.TryGetValues("request-id", out var rids) ? rids.FirstOrDefault() : null;
        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            retryAfter = delta;
        }
        else if (response.Headers.TryGetValues("Retry-After", out var raVals) &&
            int.TryParse(raVals.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
        {
            retryAfter = TimeSpan.FromSeconds(secs);
        }

        string? graphMessage = null;
        try
        {
            using var errDoc = JsonDocument.Parse(rawBody);
            if (errDoc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg))
            {
                graphMessage = msg.GetString();
            }
        }
        catch (JsonException)
        {
            graphMessage = rawBody;
        }

        response.Dispose();
        var isTransient = statusCode is (int)HttpStatusCode.TooManyRequests or 502 or 503 or 504 or 500;
        throw new StepExecutionException(
            PlainLanguageErrorMapper.MapGraphError(actionDescription, statusCode, graphMessage),
            $"HTTP {statusCode} {method} {relativePath}: {rawBody}",
            isTransient: isTransient,
            retryAfter: retryAfter,
            graphRequestId: graphRequestId,
            graphClientRequestId: clientRequestId);
    }

    private static DirectoryUserDto ParseUser(JsonElement el)
    {
        var phones = el.TryGetProperty("businessPhones", out var bp) && bp.ValueKind == JsonValueKind.Array
            ? bp.EnumerateArray().Select(p => p.GetString()).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))
            : null;
        var licenseCount = el.TryGetProperty("assignedLicenses", out var al) && al.ValueKind == JsonValueKind.Array
            ? al.GetArrayLength()
            : 0;
        DateTimeOffset? created = el.TryGetProperty("createdDateTime", out var ct) && ct.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(ct.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

        return new DirectoryUserDto(
            Id: GetString(el, "id") ?? string.Empty,
            UserPrincipalName: GetString(el, "userPrincipalName") ?? string.Empty,
            DisplayName: GetString(el, "displayName") ?? string.Empty,
            Mail: GetString(el, "mail"),
            JobTitle: GetString(el, "jobTitle"),
            Department: GetString(el, "department"),
            OfficeLocation: GetString(el, "officeLocation"),
            EmployeeId: GetString(el, "employeeId"),
            UsageLocation: GetString(el, "usageLocation"),
            CompanyName: GetString(el, "companyName"),
            BusinessPhone: phones,
            MobilePhone: GetString(el, "mobilePhone"),
            AccountEnabled: GetBool(el, "accountEnabled", defaultValue: true),
            OnPremisesSyncEnabled: GetBool(el, "onPremisesSyncEnabled", defaultValue: false),
            OnPremisesSamAccountName: GetString(el, "onPremisesSamAccountName"),
            OnPremisesDistinguishedName: GetString(el, "onPremisesDistinguishedName"),
            UserType: GetString(el, "userType") ?? "Member",
            LicenseCount: licenseCount,
            CreatedDateTime: created);
    }

    private static DirectoryGroupDto ParseGroup(JsonElement el)
    {
        var groupTypes = el.TryGetProperty("groupTypes", out var gt) && gt.ValueKind == JsonValueKind.Array
            ? gt.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList()
            : [];
        var securityEnabled = GetBool(el, "securityEnabled", false);
        var mailEnabled = GetBool(el, "mailEnabled", false);

        var type = groupTypes.Contains("Unified", StringComparer.OrdinalIgnoreCase)
            ? "Microsoft 365"
            : mailEnabled && securityEnabled
                ? "Mail-enabled security"
                : mailEnabled
                    ? "Distribution list"
                    : "Security";

        return new DirectoryGroupDto(
            Id: GetString(el, "id") ?? string.Empty,
            DisplayName: GetString(el, "displayName") ?? string.Empty,
            Mail: GetString(el, "mail"),
            Description: GetString(el, "description"),
            GroupType: type,
            OnPremisesSyncEnabled: GetBool(el, "onPremisesSyncEnabled", false),
            OnPremisesDistinguishedName: GetString(el, "onPremisesDistinguishedName"),
            MemberCount: el.TryGetProperty("memberCount", out var mc) && mc.ValueKind == JsonValueKind.Number ? mc.GetInt32() : 0);
    }

    private static ConditionalAccessPolicyDto ParseCaPolicy(JsonElement el)
    {
        var id = GetString(el, "id") ?? string.Empty;
        var displayName = GetString(el, "displayName") ?? string.Empty;
        var state = GetString(el, "state") ?? "disabled";
        var modified = GetString(el, "modifiedDateTime") ?? string.Empty;

        var includeUsers = new List<string>();
        var excludeUsers = new List<string>();
        var includeGroups = new List<string>();
        var excludeGroups = new List<string>();
        var includeRoles = new List<string>();
        var excludeRoles = new List<string>();
        var blocksLegacyAuth = false;
        var conditionsSummary = "All cloud apps";

        if (el.TryGetProperty("conditions", out var cond) && cond.ValueKind == JsonValueKind.Object)
        {
            if (cond.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Object)
            {
                includeUsers = GetStringArray(users, "includeUsers");
                excludeUsers = GetStringArray(users, "excludeUsers");
                includeGroups = GetStringArray(users, "includeGroups");
                excludeGroups = GetStringArray(users, "excludeGroups");
                includeRoles = GetStringArray(users, "includeRoles");
                excludeRoles = GetStringArray(users, "excludeRoles");
            }

            if (cond.TryGetProperty("clientAppTypes", out var capps) && capps.ValueKind == JsonValueKind.Array)
            {
                var appTypes = capps.EnumerateArray().Select(x => x.GetString() ?? string.Empty).ToList();
                blocksLegacyAuth = appTypes.Contains("exchangeActiveSync", StringComparer.OrdinalIgnoreCase)
                    || appTypes.Contains("other", StringComparer.OrdinalIgnoreCase);
            }

            if (cond.TryGetProperty("conditionsSummary", out var cs) && cs.ValueKind == JsonValueKind.String)
            {
                conditionsSummary = cs.GetString() ?? conditionsSummary;
            }
        }

        var grantControls = new List<string>();
        if (el.TryGetProperty("grantControls", out var gc) && gc.ValueKind == JsonValueKind.Object &&
            gc.TryGetProperty("builtInControls", out var bic) && bic.ValueKind == JsonValueKind.Array)
        {
            grantControls = bic.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList();
        }

        var requiresMfa = grantControls.Contains("mfa", StringComparer.OrdinalIgnoreCase);
        var etagInput = $"{id}|{state}|{modified}|{string.Join(',', includeUsers)}|{string.Join(',', excludeUsers)}|{string.Join(',', includeGroups)}|{string.Join(',', excludeGroups)}";
        var etag = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(etagInput)))[..16];

        return new ConditionalAccessPolicyDto(
            id,
            displayName,
            state,
            etag,
            includeUsers,
            excludeUsers,
            includeGroups,
            excludeGroups,
            includeRoles,
            excludeRoles,
            grantControls,
            requiresMfa,
            blocksLegacyAuth,
            conditionsSummary,
            []);
    }

    private static string? GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String ? val.GetString() : null;

    private static bool GetBool(JsonElement el, string prop, bool defaultValue) =>
        el.TryGetProperty(prop, out var val) && val.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? val.GetBoolean()
            : defaultValue;

    private static List<string> GetStringArray(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0).ToList()
            : [];
}

public static class FriendlySkuNames
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SPE_E3"] = "Microsoft 365 E3",
        ["SPE_E5"] = "Microsoft 365 E5",
        ["SPB"] = "Microsoft 365 Business Premium",
        ["O365_BUSINESS_ESSENTIALS"] = "Microsoft 365 Business Basic",
        ["O365_BUSINESS_PREMIUM"] = "Microsoft 365 Business Standard",
        ["EXCHANGESTANDARD"] = "Exchange Online (Plan 1)",
        ["EXCHANGEENTERPRISE"] = "Exchange Online (Plan 2)",
        ["EMSPREMIUM"] = "Enterprise Mobility + Security E5",
        ["EMS"] = "Enterprise Mobility + Security E3",
        ["FLOW_FREE"] = "Microsoft Power Automate Free",
        ["TEAMS_EXPLORATORY"] = "Microsoft Teams Exploratory",
    };

    public static string Resolve(string skuPartNumber) =>
        Map.TryGetValue(skuPartNumber, out var friendly) ? friendly : skuPartNumber;
}
