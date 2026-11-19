// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.Tokens;

namespace Rollcall.TestSupport;

public sealed record FakeGraphCall(
    string Method,
    string PathAndQuery,
    string? Body,
    DateTimeOffset Timestamp);

public sealed record FakeGraphFault(
    HttpStatusCode StatusCode,
    string Message,
    int? RetryAfterSeconds = null);

public sealed class FakeGraphUser
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string UserPrincipalName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Mail { get; set; }
    public string? MailNickname { get; set; }
    public string? GivenName { get; set; }
    public string? Surname { get; set; }
    public string? JobTitle { get; set; }
    public string? Department { get; set; }
    public string? OfficeLocation { get; set; }
    public string? EmployeeId { get; set; }
    public string? UsageLocation { get; set; }
    public string? CompanyName { get; set; }
    public List<string> BusinessPhones { get; set; } = [];
    public string? MobilePhone { get; set; }
    public bool AccountEnabled { get; set; } = true;
    public bool OnPremisesSyncEnabled { get; set; }
    public string? OnPremisesSamAccountName { get; set; }
    public string? OnPremisesDistinguishedName { get; set; }
    public string UserType { get; set; } = "Member";
    public string? ManagerId { get; set; }
    public List<string> AssignedSkuIds { get; set; } = [];
    public Dictionary<string, string> GroupAssignedSkus { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FakeMfaMethod> MfaMethods { get; set; } = [];
    public List<string> DirectoryRoles { get; set; } = [];
    public List<string> OwnedGroupIds { get; set; } = [];
    public Dictionary<string, string> OneDrivePermissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int SessionsRevokedCount { get; set; }
    public int PasswordResetCount { get; set; }
    public DateTimeOffset CreatedDateTime { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FakeMfaMethod
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string ODataType { get; set; } = "#microsoft.graph.microsoftAuthenticatorAuthenticationMethod";
    public string DisplayName { get; set; } = "Microsoft Authenticator";
    public string? PhoneNumber { get; set; }
}

public sealed class FakeGraphGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string DisplayName { get; set; } = string.Empty;
    public string? Mail { get; set; }
    public string? Description { get; set; }
    public List<string> GroupTypes { get; set; } = [];
    public bool SecurityEnabled { get; set; } = true;
    public bool MailEnabled { get; set; }
    public bool OnPremisesSyncEnabled { get; set; }
    public string? OnPremisesDistinguishedName { get; set; }
    public HashSet<string> MemberIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> OwnerIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeGraphSku
{
    public string SkuId { get; set; } = Guid.NewGuid().ToString("D");
    public string SkuPartNumber { get; set; } = "SPE_E3";
    public int EnabledUnits { get; set; } = 50;
    public int ConsumedUnits { get; set; } = 10;
}

public sealed class FakeCaPolicy
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string DisplayName { get; set; } = string.Empty;
    public string State { get; set; } = "enabled";
    public DateTimeOffset ModifiedDateTime { get; set; } = DateTimeOffset.UtcNow;
    public List<string> IncludeUsers { get; set; } = ["All"];
    public List<string> ExcludeUsers { get; set; } = [];
    public List<string> IncludeGroups { get; set; } = [];
    public List<string> ExcludeGroups { get; set; } = [];
    public List<string> IncludeRoles { get; set; } = [];
    public List<string> ExcludeRoles { get; set; } = [];
    public List<string> ClientAppTypes { get; set; } = ["browser", "mobileAppsAndDesktopClients"];
    public List<string> GrantControls { get; set; } = ["mfa"];
    public string ConditionsSummary { get; set; } = "All cloud apps";
}

/// <summary>
/// Full in-memory Microsoft Graph v1.0 + Entra token endpoint fake (implementation-plan.md 6.2).
/// Supports $select enforcement, $batch, failure injection (429 Retry-After, 403, 500),
/// simulated Entra Connect sync delay, call logging, and a 200-user Contoso seed dataset.
/// </summary>
public sealed class FakeGraphHandler : HttpMessageHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly RSA _tokenSigningKey = RSA.Create(2048);
    private readonly object _lock = new();

    public string TenantId { get; set; } = "11111111-2222-3333-4444-555555555555";
    public string OrganizationName { get; set; } = "Contoso Ltd";
    public string PrimaryDomain { get; set; } = "contoso.com";
    public bool EnforceSelectOnReads { get; set; } = true;
    public int OneDriveRetentionDays { get; set; } = 30;

    public List<string> GrantedAppPermissions { get; } =
    [
        "User.Read.All",
        "Group.Read.All",
        "Organization.Read.All",
        "Directory.Read.All",
        "AuditLog.Read.All",
        "User.ReadWrite.All",
        "User-PasswordProfile.ReadWrite.All",
        "User.RevokeSessions.All",
        "User.EnableDisableAccount.All",
        "GroupMember.ReadWrite.All",
        "LicenseAssignment.ReadWrite.All",
        "Policy.Read.All",
        "Policy.ReadWrite.ConditionalAccess",
        "UserAuthenticationMethod.ReadWrite.All",
        "Exchange.ManageAsApp",
        "Sites.FullControl.All",
    ];

    public List<string> AssignedDirectoryRoles { get; } =
    [
        "Exchange Administrator",
        "User Administrator",
        "Authentication Administrator",
    ];

    public Dictionary<string, FakeGraphUser> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FakeGraphGroup> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FakeGraphSku> Skus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FakeCaPolicy> CaPolicies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentQueue<FakeGraphCall> CallLog { get; } = new();
    public Dictionary<string, Queue<FakeGraphFault>> InjectedFaults { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, (int PollsRemaining, FakeGraphUser PendingUser)> DelayedSyncUsers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ReadsMissingSelect { get; } = [];

    public FakeGraphHandler()
    {
        SeedDefaultContosoData(200);
    }

    public void InjectFault(string methodAndPathPrefix, FakeGraphFault fault)
    {
        lock (_lock)
        {
            if (!InjectedFaults.TryGetValue(methodAndPathPrefix, out var queue))
            {
                queue = new Queue<FakeGraphFault>();
                InjectedFaults[methodAndPathPrefix] = queue;
            }

            queue.Enqueue(fault);
        }
    }

    public void ScheduleSimulatedSyncUser(FakeGraphUser user, int pollsUntilVisible)
    {
        lock (_lock)
        {
            var key = user.OnPremisesSamAccountName ?? user.UserPrincipalName;
            DelayedSyncUsers[key] = (pollsUntilVisible, user);
        }
    }

    public void SeedDefaultContosoData(int userCount)
    {
        Users.Clear();
        Groups.Clear();
        Skus.Clear();
        CaPolicies.Clear();

        var skuE3 = new FakeGraphSku { SkuId = "sku-e3", SkuPartNumber = "SPE_E3", EnabledUnits = 250, ConsumedUnits = 150 };
        var skuE5 = new FakeGraphSku { SkuId = "sku-e5", SkuPartNumber = "SPE_E5", EnabledUnits = 50, ConsumedUnits = 12 };
        var skuBp = new FakeGraphSku { SkuId = "sku-bp", SkuPartNumber = "SPB", EnabledUnits = 100, ConsumedUnits = 40 };
        var skuZero = new FakeGraphSku { SkuId = "sku-zero", SkuPartNumber = "EXCHANGEENTERPRISE", EnabledUnits = 10, ConsumedUnits = 10 };
        var skuFlow = new FakeGraphSku { SkuId = "sku-flow", SkuPartNumber = "FLOW_FREE", EnabledUnits = 1000, ConsumedUnits = 20 };
        foreach (var s in new[] { skuE3, skuE5, skuBp, skuZero, skuFlow })
        {
            Skus[s.SkuId] = s;
        }

        string[] departments = ["Sales", "Engineering", "Finance", "Operations", "IT", "Marketing", "Legal", "HR"];
        for (var i = 1; i <= 40; i++)
        {
            var dept = departments[(i - 1) % departments.Length];
            var g = new FakeGraphGroup
            {
                Id = $"grp-{i:D3}",
                DisplayName = i == 1 ? "Sales - All Staff" : i == 2 ? "All Employees" : $"{dept} Team {i}",
                Mail = i <= 10 ? $"dl-{dept.ToLowerInvariant()}-{i}@{PrimaryDomain}" : null,
                Description = $"Contoso {dept} group #{i}",
                GroupTypes = i % 3 == 0 ? ["Unified"] : [],
                SecurityEnabled = i > 5,
                MailEnabled = i <= 10 || i % 3 == 0,
                OnPremisesSyncEnabled = i > 35,
                OnPremisesDistinguishedName = i > 35 ? $"CN={dept} Team {i},OU=Groups,DC=contoso,DC=local" : null,
            };
            Groups[g.Id] = g;
        }

        for (var i = 1; i <= userCount; i++)
        {
            var dept = departments[(i - 1) % departments.Length];
            var uid = $"user-{i:D3}";
            var upn = i == 1 ? $"alex.chen@{PrimaryDomain}"
                : i == 2 ? $"jordan.lee@{PrimaryDomain}"
                : i == 3 ? $"breakglass@{PrimaryDomain}"
                : $"user{i:D3}@{PrimaryDomain}";
            var name = i == 1 ? "Alex Chen"
                : i == 2 ? "Jordan Lee"
                : i == 3 ? "Break Glass Admin"
                : $"Contoso User {i:D3}";
            var synced = i % 5 == 0;
            var enabled = i % 15 != 0;
            var u = new FakeGraphUser
            {
                Id = uid,
                UserPrincipalName = upn,
                Mail = upn,
                MailNickname = upn.Split('@')[0],
                DisplayName = name,
                GivenName = name.Split(' ')[0],
                Surname = name.Split(' ')[^1],
                JobTitle = i == 2 ? "Sales Associate" : $"{dept} Specialist",
                Department = dept,
                OfficeLocation = i % 2 == 0 ? "Seattle" : "Austin",
                EmployeeId = $"EMP{i:D4}",
                UsageLocation = i == 4 ? null : "US",
                CompanyName = "Contoso Ltd",
                BusinessPhones = [$"+1-555-01{i:D2}"],
                AccountEnabled = enabled,
                OnPremisesSyncEnabled = synced,
                OnPremisesSamAccountName = synced ? $"u{i:D3}" : null,
                OnPremisesDistinguishedName = synced ? $"CN={name},OU=Staff,DC=contoso,DC=local" : null,
                UserType = i == 20 ? "Guest" : "Member",
                ManagerId = i > 1 ? "user-001" : null,
                AssignedSkuIds = i % 4 == 0 ? [] : ["sku-e3"],
                MfaMethods = i == 6 ? [] :
                [
                    new FakeMfaMethod
                    {
                        Id = $"mfa-{i:D3}",
                        ODataType = "#microsoft.graph.microsoftAuthenticatorAuthenticationMethod",
                        DisplayName = "Microsoft Authenticator",
                    },
                ],
                DirectoryRoles = i == 1 ? ["Global Administrator"] : [],
            };

            if (i == 2)
            {
                Groups["grp-001"].MemberIds.Add(u.Id);
                Groups["grp-002"].MemberIds.Add(u.Id);
            }

            Users[u.Id] = u;
        }

        CaPolicies["ca-001"] = new FakeCaPolicy
        {
            Id = "ca-001",
            DisplayName = "Require MFA for all users",
            State = "enabled",
            IncludeUsers = ["All"],
            ExcludeUsers = ["user-003"],
            GrantControls = ["mfa"],
        };
        CaPolicies["ca-002"] = new FakeCaPolicy
        {
            Id = "ca-002",
            DisplayName = "Block legacy authentication",
            State = "enabled",
            IncludeUsers = ["All"],
            ExcludeUsers = ["user-003"],
            ClientAppTypes = ["exchangeActiveSync", "other"],
            GrantControls = ["block"],
        };
        CaPolicies["ca-003"] = new FakeCaPolicy
        {
            Id = "ca-003",
            DisplayName = "Require MFA for Sales (report-only)",
            State = "enabledForReportingButNotEnforced",
            IncludeUsers = [],
            IncludeGroups = ["grp-001"],
            GrantControls = ["mfa"],
        };
        CaPolicies["ca-004"] = new FakeCaPolicy
        {
            Id = "ca-004",
            DisplayName = "Require compliant device for Admins",
            State = "enabled",
            IncludeUsers = [],
            IncludeRoles = ["Global Administrator"],
            GrantControls = ["compliantDevice"],
            ConditionsSummary = "All cloud apps, Windows/macOS",
        };
        CaPolicies["ca-005"] = new FakeCaPolicy
        {
            Id = "ca-005",
            DisplayName = "Legacy VPN policy (off)",
            State = "disabled",
            IncludeUsers = ["All"],
            GrantControls = ["mfa"],
        };
        CaPolicies["ca-006"] = new FakeCaPolicy
        {
            Id = "ca-006",
            DisplayName = "Restrict guest access",
            State = "enabled",
            IncludeUsers = ["GuestsOrExternalUsers"],
            GrantControls = ["mfa"],
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is not null ? await request.Content.ReadAsStringAsync(cancellationToken) : null;
        var pathAndQuery = request.RequestUri!.PathAndQuery;
        var path = NormalizeGraphPath(request.RequestUri.AbsolutePath);

        CallLog.Enqueue(new FakeGraphCall(request.Method.Method, pathAndQuery, body, DateTimeOffset.UtcNow));

        // Token endpoint
        if (path.EndsWith("/oauth2/v2.0/token", StringComparison.OrdinalIgnoreCase))
        {
            return HandleTokenEndpoint();
        }

        // Fault injection check
        var fault = DequeueMatchingFault(request.Method.Method, path);
        if (fault is not null)
        {
            var errResp = JsonResponse(fault.StatusCode, new { error = new { code = fault.StatusCode.ToString(), message = fault.Message } });
            errResp.Headers.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString("D"));
            if (fault.RetryAfterSeconds.HasValue)
            {
                errResp.Headers.TryAddWithoutValidation("Retry-After", fault.RetryAfterSeconds.Value.ToString(CultureInfo.InvariantCulture));
            }

            return errResp;
        }

        // M4 criterion 3: enforce $select on every Graph GET read!
        if (request.Method == HttpMethod.Get && EnforceSelectOnReads && !pathAndQuery.Contains("$select=", StringComparison.OrdinalIgnoreCase))
        {
            lock (_lock)
            {
                ReadsMissingSelect.Add(pathAndQuery);
            }

            return JsonResponse(HttpStatusCode.BadRequest, new { error = new { code = "MissingSelect", message = $"FakeGraph requires $select on GET {pathAndQuery}" } });
        }

        lock (_lock)
        {
            return DispatchGraphRequest(request.Method, path, request.RequestUri.Query, body);
        }
    }

    private HttpResponseMessage HandleTokenEndpoint()
    {
        var claims = new List<Claim>
        {
            new("tid", TenantId),
            new("aud", "https://graph.microsoft.com"),
        };
        lock (_lock)
        {
            foreach (var role in GrantedAppPermissions)
            {
                claims.Add(new Claim("roles", role));
            }
        }

        var creds = new SigningCredentials(new RsaSecurityKey(_tokenSigningKey), SecurityAlgorithms.RsaSha256);
        var jwt = new JwtSecurityToken(
            issuer: $"https://sts.windows.net/{TenantId}/",
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);
        var tokenStr = new JwtSecurityTokenHandler().WriteToken(jwt);
        return JsonResponse(HttpStatusCode.OK, new { access_token = tokenStr, token_type = "Bearer", expires_in = 3600 });
    }

    private HttpResponseMessage DispatchGraphRequest(HttpMethod method, string path, string query, string? body)
    {
        var qs = HttpUtility.ParseQueryString(query);

        if (method == HttpMethod.Get && path == "/organization")
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                value = new[]
                {
                    new
                    {
                        id = TenantId,
                        displayName = OrganizationName,
                        verifiedDomains = new[] { new { name = PrimaryDomain, isDefault = true } },
                    },
                },
            });
        }

        if (method == HttpMethod.Get && path == "/servicePrincipals")
        {
            return JsonResponse(HttpStatusCode.OK, new { value = new[] { new { id = "sp-rollcall", appId = "rollcall-app" } } });
        }

        if (method == HttpMethod.Get && path.StartsWith("/servicePrincipals/", StringComparison.Ordinal) && path.EndsWith("/memberOf", StringComparison.Ordinal))
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                value = AssignedDirectoryRoles.Select(r => new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.directoryRole",
                    ["id"] = r,
                    ["displayName"] = r,
                }),
            });
        }

        if (method == HttpMethod.Post && path == "/$batch")
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            var responses = new List<object>();
            foreach (var req in doc.RootElement.GetProperty("requests").EnumerateArray())
            {
                var id = req.GetProperty("id").GetString() ?? string.Empty;
                var m = new HttpMethod(req.GetProperty("method").GetString() ?? "GET");
                var u = req.GetProperty("url").GetString() ?? "/";
                var subBody = req.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null ? b.GetRawText() : null;
                var subResp = DispatchGraphRequest(m, NormalizeGraphPath(u), string.Empty, subBody);
                responses.Add(new { id, status = (int)subResp.StatusCode });
            }

            return JsonResponse(HttpStatusCode.OK, new { responses });
        }

        if (method == HttpMethod.Get && path == "/users")
        {
            AdvanceDelayedSyncUsers();
            var list = Users.Values.AsEnumerable();
            var search = qs["$search"]?.Trim('"');
            if (!string.IsNullOrWhiteSpace(search))
            {
                list = list.Where(u =>
                    u.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    u.UserPrincipalName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    (u.Mail?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (u.EmployeeId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            var filter = qs["$filter"];
            if (!string.IsNullOrWhiteSpace(filter))
            {
                list = ApplyUserFilter(list, filter);
            }

            var top = int.TryParse(qs["$top"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) ? t : 200;
            return JsonResponse(HttpStatusCode.OK, new { value = list.Take(top).Select(SerializeUser) });
        }

        if (method == HttpMethod.Post && path == "/users")
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            var r = doc.RootElement;
            var upn = r.GetProperty("userPrincipalName").GetString()!;
            var user = new FakeGraphUser
            {
                Id = Guid.NewGuid().ToString("D"),
                UserPrincipalName = upn,
                Mail = upn,
                MailNickname = r.TryGetProperty("mailNickname", out var mn) ? mn.GetString() : upn.Split('@')[0],
                DisplayName = r.GetProperty("displayName").GetString()!,
                GivenName = r.TryGetProperty("givenName", out var gn) ? gn.GetString() : null,
                Surname = r.TryGetProperty("surname", out var sn) ? sn.GetString() : null,
                AccountEnabled = !r.TryGetProperty("accountEnabled", out var ae) || ae.GetBoolean(),
                Department = r.TryGetProperty("department", out var d) ? d.GetString() : null,
                JobTitle = r.TryGetProperty("jobTitle", out var jt) ? jt.GetString() : null,
                OfficeLocation = r.TryGetProperty("officeLocation", out var ol) ? ol.GetString() : null,
                CompanyName = r.TryGetProperty("companyName", out var cn) ? cn.GetString() : null,
                UsageLocation = r.TryGetProperty("usageLocation", out var ul) ? ul.GetString() : null,
            };
            Users[user.Id] = user;
            return JsonResponse(HttpStatusCode.Created, SerializeUser(user));
        }

        if (path.StartsWith("/users/", StringComparison.Ordinal))
        {
            return HandleUserSubPath(method, path, body);
        }

        if (method == HttpMethod.Get && path == "/groups")
        {
            var list = Groups.Values.AsEnumerable();
            var search = qs["$search"]?.Trim('"');
            if (!string.IsNullOrWhiteSpace(search))
            {
                list = list.Where(g => g.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase));
            }

            return JsonResponse(HttpStatusCode.OK, new { value = list.Select(SerializeGroup) });
        }

        if (path.StartsWith("/groups/", StringComparison.Ordinal))
        {
            return HandleGroupSubPath(method, path, body);
        }

        if (method == HttpMethod.Get && path == "/subscribedSkus")
        {
            return JsonResponse(HttpStatusCode.OK, new
            {
                value = Skus.Values.Select(s => new
                {
                    skuId = s.SkuId,
                    skuPartNumber = s.SkuPartNumber,
                    prepaidUnits = new { enabled = s.EnabledUnits },
                    consumedUnits = s.ConsumedUnits,
                    servicePlans = new[] { new { servicePlanId = "plan-exo", servicePlanName = "EXCHANGE_S_ENTERPRISE", appliesTo = "User" } },
                }),
            });
        }

        if (method == HttpMethod.Get && path == "/identity/conditionalAccess/policies")
        {
            return JsonResponse(HttpStatusCode.OK, new { value = CaPolicies.Values.Select(SerializeCaPolicy) });
        }

        if (path.StartsWith("/identity/conditionalAccess/policies/", StringComparison.Ordinal))
        {
            var policyId = Uri.UnescapeDataString(path["/identity/conditionalAccess/policies/".Length..]);
            if (!CaPolicies.TryGetValue(policyId, out var policy))
            {
                return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "NotFound", message = "Policy not found." } });
            }

            if (method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, SerializeCaPolicy(policy));
            }

            if (method == HttpMethod.Patch)
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                if (doc.RootElement.TryGetProperty("conditions", out var cond) && cond.TryGetProperty("users", out var u))
                {
                    if (u.TryGetProperty("includeUsers", out var iu))
                    {
                        policy.IncludeUsers = iu.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }

                    if (u.TryGetProperty("excludeUsers", out var eu))
                    {
                        policy.ExcludeUsers = eu.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }

                    if (u.TryGetProperty("includeGroups", out var ig))
                    {
                        policy.IncludeGroups = ig.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }

                    if (u.TryGetProperty("excludeGroups", out var eg))
                    {
                        policy.ExcludeGroups = eg.EnumerateArray().Select(x => x.GetString()!).ToList();
                    }
                }

                policy.ModifiedDateTime = DateTimeOffset.UtcNow;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (method == HttpMethod.Get && path == "/admin/sharepoint/settings")
        {
            return JsonResponse(HttpStatusCode.OK, new { deletedUserPersonalSiteRetentionPeriodInDays = OneDriveRetentionDays });
        }

        return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "NotFound", message = $"Unhandled FakeGraph route {method} {path}" } });
    }

    private HttpResponseMessage HandleUserSubPath(HttpMethod method, string path, string? body)
    {
        var parts = path["/users/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var key = Uri.UnescapeDataString(parts[0]);
        AdvanceDelayedSyncUsers();
        var user = FindUser(key);
        if (user is null)
        {
            return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "Request_ResourceNotFound", message = $"User {key} not found." } });
        }

        if (parts.Length == 1)
        {
            if (method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, SerializeUser(user));
            }

            if (method == HttpMethod.Patch)
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var root = doc.RootElement;
                if (root.TryGetProperty("jobTitle", out var jt))
                {
                    user.JobTitle = jt.ValueKind == JsonValueKind.Null ? null : jt.GetString();
                }

                if (root.TryGetProperty("department", out var d))
                {
                    user.Department = d.ValueKind == JsonValueKind.Null ? null : d.GetString();
                }

                if (root.TryGetProperty("officeLocation", out var ol))
                {
                    user.OfficeLocation = ol.ValueKind == JsonValueKind.Null ? null : ol.GetString();
                }

                if (root.TryGetProperty("companyName", out var cn))
                {
                    user.CompanyName = cn.ValueKind == JsonValueKind.Null ? null : cn.GetString();
                }

                if (root.TryGetProperty("employeeId", out var eid))
                {
                    user.EmployeeId = eid.ValueKind == JsonValueKind.Null ? null : eid.GetString();
                }

                if (root.TryGetProperty("usageLocation", out var ul))
                {
                    user.UsageLocation = ul.ValueKind == JsonValueKind.Null ? null : ul.GetString();
                }

                if (root.TryGetProperty("mobilePhone", out var mp))
                {
                    user.MobilePhone = mp.ValueKind == JsonValueKind.Null ? null : mp.GetString();
                }

                if (root.TryGetProperty("accountEnabled", out var ae))
                {
                    user.AccountEnabled = ae.GetBoolean();
                }

                if (root.TryGetProperty("passwordProfile", out _))
                {
                    user.PasswordResetCount++;
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts[1] == "manager")
        {
            if (method == HttpMethod.Get)
            {
                var mgr = user.ManagerId is not null ? FindUser(user.ManagerId) : null;
                return mgr is null
                    ? JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "Request_ResourceNotFound", message = "No manager." } })
                    : JsonResponse(HttpStatusCode.OK, SerializeUser(mgr));
            }

            if (method == HttpMethod.Delete)
            {
                user.ManagerId = null;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Put)
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var odataId = doc.RootElement.GetProperty("@odata.id").GetString() ?? string.Empty;
                user.ManagerId = odataId.Split('/')[^1];
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts[1] == "memberOf")
        {
            var items = new List<object>();
            foreach (var g in Groups.Values.Where(g => g.MemberIds.Contains(user.Id)))
            {
                items.Add(SerializeGroup(g));
            }

            foreach (var role in user.DirectoryRoles)
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["@odata.type"] = "#microsoft.graph.directoryRole",
                    ["id"] = role,
                    ["displayName"] = role,
                });
            }

            return JsonResponse(HttpStatusCode.OK, new { value = items });
        }

        if (parts[1] == "ownedObjects")
        {
            var owned = Groups.Values
                .Where(g => g.OwnerIds.Contains(user.Id) || user.OwnedGroupIds.Contains(g.Id))
                .Select(g => new { id = g.Id, displayName = g.DisplayName })
                .ToList();
            return JsonResponse(HttpStatusCode.OK, new { value = owned });
        }

        if (parts[1] == "revokeSignInSessions" && method == HttpMethod.Post)
        {
            user.SessionsRevokedCount++;
            return JsonResponse(HttpStatusCode.OK, new { value = true });
        }

        if (parts[1] == "assignLicense" && method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(body ?? "{}");
            if (doc.RootElement.TryGetProperty("addLicenses", out var add))
            {
                foreach (var item in add.EnumerateArray())
                {
                    var skuId = item.GetProperty("skuId").GetString()!;
                    if (!user.AssignedSkuIds.Contains(skuId, StringComparer.OrdinalIgnoreCase))
                    {
                        user.AssignedSkuIds.Add(skuId);
                        if (Skus.TryGetValue(skuId, out var sku))
                        {
                            sku.ConsumedUnits++;
                        }
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("removeLicenses", out var rem))
            {
                foreach (var item in rem.EnumerateArray())
                {
                    var skuId = item.GetString()!;
                    if (user.AssignedSkuIds.RemoveAll(x => string.Equals(x, skuId, StringComparison.OrdinalIgnoreCase)) > 0 &&
                        Skus.TryGetValue(skuId, out var sku))
                    {
                        sku.ConsumedUnits = Math.Max(0, sku.ConsumedUnits - 1);
                    }
                }
            }

            return JsonResponse(HttpStatusCode.OK, SerializeUser(user));
        }

        if (parts[1] == "authentication")
        {
            if (parts.Length == 3 && parts[2] == "methods" && method == HttpMethod.Get)
            {
                return JsonResponse(HttpStatusCode.OK, new
                {
                    value = user.MfaMethods.Select(m => new Dictionary<string, object?>
                    {
                        ["@odata.type"] = m.ODataType,
                        ["id"] = m.Id,
                        ["displayName"] = m.DisplayName,
                        ["phoneNumber"] = m.PhoneNumber,
                    }),
                });
            }

            if (parts.Length == 3 && parts[2] == "temporaryAccessPassMethods" && method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var mins = doc.RootElement.TryGetProperty("lifetimeInMinutes", out var lm) ? lm.GetInt32() : 60;
                var once = !doc.RootElement.TryGetProperty("isUsableOnce", out var uo) || uo.GetBoolean();
                var tapVal = "Kx7mQp2R9tVw";
                return JsonResponse(HttpStatusCode.Created, new
                {
                    id = Guid.NewGuid().ToString("D"),
                    temporaryAccessPass = tapVal,
                    startDateTime = DateTimeOffset.UtcNow,
                    lifetimeInMinutes = mins,
                    isUsableOnce = once,
                });
            }

            if (parts.Length == 4 && method == HttpMethod.Delete)
            {
                var mid = Uri.UnescapeDataString(parts[3]);
                user.MfaMethods.RemoveAll(m => string.Equals(m.Id, mid, StringComparison.OrdinalIgnoreCase));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts[1] == "drive")
        {
            if (method == HttpMethod.Post && path.EndsWith("/invite", StringComparison.Ordinal))
            {
                var permId = "od-perm-" + Guid.NewGuid().ToString("N")[..8];
                user.OneDrivePermissions[permId] = body ?? string.Empty;
                return JsonResponse(HttpStatusCode.OK, new { value = new[] { new { id = permId } } });
            }

            if (method == HttpMethod.Delete && parts.Length >= 5 && parts[3] == "permissions")
            {
                var permId = Uri.UnescapeDataString(parts[4]);
                user.OneDrivePermissions.Remove(permId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "NotFound", message = $"Unhandled user path {path}" } });
    }

    private HttpResponseMessage HandleGroupSubPath(HttpMethod method, string path, string? body)
    {
        var parts = path["/groups/".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var groupId = Uri.UnescapeDataString(parts[0]);
        if (!Groups.TryGetValue(groupId, out var group))
        {
            return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "Request_ResourceNotFound", message = $"Group {groupId} not found." } });
        }

        if (parts.Length == 1 && method == HttpMethod.Get)
        {
            return JsonResponse(HttpStatusCode.OK, SerializeGroup(group));
        }

        if (parts[1] == "members")
        {
            if (method == HttpMethod.Get)
            {
                var members = group.MemberIds
                    .Select(id => Users.TryGetValue(id, out var u) ? u : null)
                    .Where(u => u is not null)
                    .Select(u => SerializeUser(u!));
                return JsonResponse(HttpStatusCode.OK, new { value = members });
            }

            if (method == HttpMethod.Post)
            {
                using var doc = JsonDocument.Parse(body ?? "{}");
                var odataId = doc.RootElement.GetProperty("@odata.id").GetString() ?? string.Empty;
                var uid = odataId.Split('/')[^1];
                group.MemberIds.Add(uid);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            if (method == HttpMethod.Delete && parts.Length >= 3)
            {
                var uid = Uri.UnescapeDataString(parts[2]);
                group.MemberIds.Remove(uid);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }

        if (parts[1] == "owners" && method == HttpMethod.Get)
        {
            var owners = group.OwnerIds
                .Select(id => Users.TryGetValue(id, out var u) ? u : null)
                .Where(u => u is not null)
                .Select(u => SerializeUser(u!));
            return JsonResponse(HttpStatusCode.OK, new { value = owners });
        }

        return JsonResponse(HttpStatusCode.NotFound, new { error = new { code = "NotFound", message = $"Unhandled group path {path}" } });
    }

    private void AdvanceDelayedSyncUsers()
    {
        var ready = new List<string>();
        foreach (var kv in DelayedSyncUsers)
        {
            var remaining = kv.Value.PollsRemaining - 1;
            if (remaining <= 0)
            {
                Users[kv.Value.PendingUser.Id] = kv.Value.PendingUser;
                ready.Add(kv.Key);
            }
            else
            {
                DelayedSyncUsers[kv.Key] = (remaining, kv.Value.PendingUser);
            }
        }

        foreach (var k in ready)
        {
            DelayedSyncUsers.Remove(k);
        }
    }

    private FakeGraphUser? FindUser(string idOrUpn)
    {
        if (Users.TryGetValue(idOrUpn, out var byId))
        {
            return byId;
        }

        return Users.Values.FirstOrDefault(u =>
            string.Equals(u.UserPrincipalName, idOrUpn, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(u.OnPremisesSamAccountName, idOrUpn, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<FakeGraphUser> ApplyUserFilter(IEnumerable<FakeGraphUser> source, string filter)
    {
        var clauses = filter.Split(" and ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var clause in clauses)
        {
            if (clause.StartsWith("department eq ", StringComparison.OrdinalIgnoreCase))
            {
                var val = clause["department eq ".Length..].Trim('\'');
                source = source.Where(u => string.Equals(u.Department, val, StringComparison.OrdinalIgnoreCase));
            }
            else if (clause.StartsWith("accountEnabled eq ", StringComparison.OrdinalIgnoreCase))
            {
                var val = bool.Parse(clause["accountEnabled eq ".Length..]);
                source = source.Where(u => u.AccountEnabled == val);
            }
            else if (clause.StartsWith("onPremisesSyncEnabled eq ", StringComparison.OrdinalIgnoreCase))
            {
                var val = bool.Parse(clause["onPremisesSyncEnabled eq ".Length..]);
                source = source.Where(u => u.OnPremisesSyncEnabled == val);
            }
            else if (clause.StartsWith("onPremisesSamAccountName eq ", StringComparison.OrdinalIgnoreCase))
            {
                var val = clause["onPremisesSamAccountName eq ".Length..].Trim('\'');
                source = source.Where(u => string.Equals(u.OnPremisesSamAccountName, val, StringComparison.OrdinalIgnoreCase));
            }
        }

        return source;
    }

    private FakeGraphFault? DequeueMatchingFault(string method, string path)
    {
        lock (_lock)
        {
            var key = $"{method} {path}";
            foreach (var kv in InjectedFaults)
            {
                if (kv.Value.Count > 0 && key.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    return kv.Value.Dequeue();
                }
            }

            return null;
        }
    }

    private static object SerializeUser(FakeGraphUser u)
    {
        var states = u.AssignedSkuIds.Select(s => new
        {
            skuId = s,
            assignedByGroup = u.GroupAssignedSkus.TryGetValue(s, out var g) ? g : null,
            disabledPlans = Array.Empty<string>(),
        }).ToList();

        return new
        {
            id = u.Id,
            userPrincipalName = u.UserPrincipalName,
            displayName = u.DisplayName,
            mail = u.Mail,
            jobTitle = u.JobTitle,
            department = u.Department,
            officeLocation = u.OfficeLocation,
            employeeId = u.EmployeeId,
            usageLocation = u.UsageLocation,
            companyName = u.CompanyName,
            businessPhones = u.BusinessPhones,
            mobilePhone = u.MobilePhone,
            accountEnabled = u.AccountEnabled,
            onPremisesSyncEnabled = u.OnPremisesSyncEnabled,
            onPremisesSamAccountName = u.OnPremisesSamAccountName,
            onPremisesDistinguishedName = u.OnPremisesDistinguishedName,
            userType = u.UserType,
            assignedLicenses = u.AssignedSkuIds.Select(s => new { skuId = s, disabledPlans = Array.Empty<string>() }),
            licenseAssignmentStates = states,
            createdDateTime = u.CreatedDateTime,
        };
    }

    private static Dictionary<string, object?> SerializeGroup(FakeGraphGroup g) => new()
    {
        ["@odata.type"] = "#microsoft.graph.group",
        ["id"] = g.Id,
        ["displayName"] = g.DisplayName,
        ["mail"] = g.Mail,
        ["description"] = g.Description,
        ["groupTypes"] = g.GroupTypes,
        ["securityEnabled"] = g.SecurityEnabled,
        ["mailEnabled"] = g.MailEnabled,
        ["onPremisesSyncEnabled"] = g.OnPremisesSyncEnabled,
        ["onPremisesDistinguishedName"] = g.OnPremisesDistinguishedName,
        ["memberCount"] = g.MemberIds.Count,
    };

    private static object SerializeCaPolicy(FakeCaPolicy p) => new
    {
        id = p.Id,
        displayName = p.DisplayName,
        state = p.State,
        modifiedDateTime = p.ModifiedDateTime.ToString("O", CultureInfo.InvariantCulture),
        conditions = new
        {
            users = new
            {
                includeUsers = p.IncludeUsers,
                excludeUsers = p.ExcludeUsers,
                includeGroups = p.IncludeGroups,
                excludeGroups = p.ExcludeGroups,
                includeRoles = p.IncludeRoles,
                excludeRoles = p.ExcludeRoles,
            },
            clientAppTypes = p.ClientAppTypes,
            conditionsSummary = p.ConditionsSummary,
        },
        grantControls = new
        {
            builtInControls = p.GrantControls,
        },
    };

    private static string NormalizeGraphPath(string path)
    {
        if (path.StartsWith("/v1.0", StringComparison.OrdinalIgnoreCase))
        {
            return path["/v1.0".Length..];
        }

        return path;
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode code, object payload) => new(code)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json"),
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tokenSigningKey.Dispose();
        }

        base.Dispose(disposing);
    }
}
