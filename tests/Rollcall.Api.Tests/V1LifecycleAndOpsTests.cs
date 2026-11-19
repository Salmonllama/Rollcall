// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Rollcall.Agent;
using Rollcall.Contracts;
using Rollcall.Infrastructure.Operations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.TestSupport;

namespace Rollcall.Api.Tests;

/// <summary>Integration tests covering M6, M7, M8, M9, and M10 acceptance criteria.</summary>
public sealed class V1LifecycleAndOpsTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task M6_templates_onboarding_collisions_scheduled_activation_and_welcome_pack_pdf_branding()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();

        // Criterion 1: Invalid group/SKU fails validation with item name
        var badDef = new RoleTemplateDefinition(
            "cloud", "{first}.{last}@{domain}", "{first} {last}", "Sales", "Sales Associate", "Contoso", "Seattle", "US",
            null, ["grp-does-not-exist"], [], [], [new TemplateLicenseSpec("sku-missing", [])],
            "tap", 60, true, true, "Pacific Standard Time", "en-US", null, ["department"], false);
        using var badResp = await admin.PostAsJsonAsync("/api/templates", new SaveRoleTemplateRequest("Broken", null, badDef));
        Assert.Equal(HttpStatusCode.BadRequest, badResp.StatusCode);
        var badText = await badResp.Content.ReadAsStringAsync();
        Assert.Contains("grp-does-not-exist", badText, StringComparison.Ordinal);
        Assert.Contains("sku-missing", badText, StringComparison.Ordinal);

        // Valid template creation + versioning
        var goodDef = badDef with { CloudGroupIds = ["grp-001"], Licenses = [new TemplateLicenseSpec("sku-e3", [])] };
        using var createdResp = await admin.PostAsJsonAsync("/api/templates", new SaveRoleTemplateRequest("Sales associate", "Standard sales hire", goodDef));
        Assert.Equal(HttpStatusCode.Created, createdResp.StatusCode);
        var tpl = await createdResp.Content.ReadFromJsonAsync<RoleTemplateDto>(RollcallTestHarness.JsonOptions);
        Assert.Equal(1, tpl!.Version);

        // Criterion 2 & 6: Onboard Jane Doe + Welcome Pack PDF Rollcall vs custom branding
        using var onboardResp = await admin.PostAsJsonAsync("/api/onboarding", new OnboardingRequest(
            tpl.Id, "Jane", "Doe", null, null, null, null, null, null, "user-001", null, null, null, null, Submit: true));
        Assert.Equal(HttpStatusCode.OK, onboardResp.StatusCode);
        var onboardDto = await onboardResp.Content.ReadFromJsonAsync<OnboardingPlanResponse>(RollcallTestHarness.JsonOptions);
        Assert.Empty(onboardDto!.Collisions);
        Assert.Equal(1, onboardDto.Run!.RoleTemplateVersion);
        await h.DrainWorkerQueueAsync();

        var jane = h.FakeGraph.Users.Values.Single(u => u.UserPrincipalName == "jane.doe@contoso.com");
        Assert.Equal("Jane Doe", jane.DisplayName);
        Assert.Equal("Sales", jane.Department);
        Assert.Contains(jane.Id, h.FakeGraph.Groups["grp-001"].MemberIds);
        Assert.Contains("sku-e3", jane.AssignedSkuIds);

        // Welcome Pack PDF default Rollcall branding + one-time credential
        var pdfBytes = await admin.GetByteArrayAsync($"/api/runs/{onboardDto.Run.Id}/welcome-pack.pdf");
        var pdfText = Encoding.ASCII.GetString(pdfBytes);
        Assert.StartsWith("%PDF-1.4", pdfText, StringComparison.Ordinal);
        Assert.Contains("Rollcall", pdfText, StringComparison.Ordinal);
        Assert.Contains("Kx7m Qp2R 9tVw", pdfText, StringComparison.Ordinal);

        // Second view shows "Credential already shown; issue a new one." and custom branding hides Rollcall
        var wpSettings = await admin.GetFromJsonAsync<WelcomePackSettingsDto>("/api/admin/settings/welcome-pack", RollcallTestHarness.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings/welcome-pack",
            wpSettings! with { HideRollcallBranding = true, OrganizationName = "Acme Corp" })).StatusCode);
        var pdf2Bytes = await admin.GetByteArrayAsync($"/api/runs/{onboardDto.Run.Id}/welcome-pack.pdf");
        var pdf2Text = Encoding.ASCII.GetString(pdf2Bytes);
        Assert.DoesNotContain("Rollcall", pdf2Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Acme Corp", pdf2Text, StringComparison.Ordinal);
        Assert.Contains("Credential already shown; issue a new one.", pdf2Text, StringComparison.Ordinal);

        // Criterion 3: Collisions block submission without inventing alternatives
        using var collisionResp = await admin.PostAsJsonAsync("/api/onboarding", new OnboardingRequest(
            tpl.Id, "Jane", "Doe", null, null, null, null, null, null, null, null, null, null, null, Submit: true));
        var colDto = await collisionResp.Content.ReadFromJsonAsync<OnboardingPlanResponse>(RollcallTestHarness.JsonOptions);
        Assert.Null(colDto!.Run);
        Assert.True(colDto.Collisions.Count >= 3);

        // Criterion 4: Scheduled future onboarding creates blocked account then activates + issues TAP at start date
        var startDate = h.Clock.GetUtcNow().AddDays(3);
        using var schedResp = await admin.PostAsJsonAsync("/api/onboarding", new OnboardingRequest(
            tpl.Id, "Future", "Hire", null, null, null, null, null, null, null, null, null, startDate, null, Submit: true));
        Assert.Equal(HttpStatusCode.OK, schedResp.StatusCode);
        await h.DrainWorkerQueueAsync();

        var futureUser = h.FakeGraph.Users.Values.Single(u => u.UserPrincipalName == "future.hire@contoso.com");
        Assert.False(futureUser.AccountEnabled);

        // Advance clock to start date -> activation run enables account and issues TAP
        h.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromMinutes(1));
        await h.DrainWorkerQueueAsync();
        Assert.True(futureUser.AccountEnabled);
    }

    [Fact]
    public async Task M7_offboarding_end_to_end_mailbox_failure_preserves_licenses_onedrive_reversal_and_restore()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();
        using var approver = await h.SignInAsAsync("oid-app", "approver@contoso.com", "Approver", [PortalRole.Approver], admin);

        // Mailbox conversion failure keeps licenses (Criterion 3)
        h.FakeExchange.FailMailboxConversion = true;
        var offReq = new OffboardingRequest(
            "user-002",
            null,
            new MailboxOffboardingOptions(true, "alex.chen@contoso.com", true, "Out of office", "External OOO", true, false, true),
            new OneDriveOffboardingOptions(true, 30),
            RemoveAllLicenses: true,
            KeepSkuIds: null,
            ClearManager: true,
            PrefixDescriptionWithLeftDate: true,
            OffboardingOuOverride: null,
            ConfirmationUpn: "jordan.lee@contoso.com",
            ConfirmPrivilegedRoleAsAdmin: false,
            Submit: true);

        using var offResp = await admin.PostAsJsonAsync("/api/offboarding", offReq);
        Assert.Equal(HttpStatusCode.OK, offResp.StatusCode);
        var offRunId = (await offResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();

        // Approve as second person
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/approvals/{offRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Approved, "Approved"))).StatusCode);
        await h.DrainWorkerQueueAsync();

        var partialRun = await admin.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{offRunId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.PartiallyFailed, partialRun!.Status);
        Assert.Contains("licenses were kept", partialRun.StatusReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sku-e3", h.FakeGraph.Users["user-002"].AssignedSkuIds);

        // Fix Exchange and retry -> completes offboarding! (Criterion 2)
        h.FakeExchange.FailMailboxConversion = false;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/runs/{offRunId}/retry", null)).StatusCode);
        await h.DrainWorkerQueueAsync();

        var doneRun = await admin.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{offRunId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.Succeeded, doneRun!.Status);
        var jordan = h.FakeGraph.Users["user-002"];
        Assert.False(jordan.AccountEnabled);
        Assert.True(jordan.SessionsRevokedCount >= 1);
        Assert.Empty(jordan.MfaMethods);
        Assert.Empty(jordan.AssignedSkuIds);
        Assert.Equal("SharedMailbox", h.FakeExchange.Mailboxes["jordan.lee@contoso.com"].RecipientTypeDetails);
        Assert.True(h.FakeExchange.Mailboxes["jordan.lee@contoso.com"].HiddenFromAddressListsEnabled);
        Assert.Contains("alex.chen@contoso.com", h.FakeExchange.Mailboxes["jordan.lee@contoso.com"].FullAccessTrustees);
        Assert.Single(jordan.OneDrivePermissions);

        // Criterion 6: Advance clock 31 days -> OneDrive access automatically revoked!
        h.Clock.Advance(TimeSpan.FromDays(31));
        await h.DrainWorkerQueueAsync();
        Assert.Empty(jordan.OneDrivePermissions);

        // Sessions expire after 8 hours; re-sign in after the 31-day time jump.
        using var adminAfterJump = await h.SignInAsAsync("oid-admin", "alex.chen@contoso.com", "Alex Chen");
        using var approverAfterJump = await h.SignInAsAsync("oid-app", "approver@contoso.com", "Approver");

        // Criterion 7: Restore user restores groups and licenses
        using var restoreResp = await adminAfterJump.PostAsJsonAsync("/api/offboarding/restore",
            new RestoreUserRequest(offRunId, true, true, true, Submit: true));
        Assert.Equal(HttpStatusCode.OK, restoreResp.StatusCode);
        var restoreRunId = (await restoreResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await approverAfterJump.PostAsJsonAsync($"/api/approvals/{restoreRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Approved, "Returning to team"))).StatusCode);
        await h.DrainWorkerQueueAsync();

        Assert.True(jordan.AccountEnabled);
        Assert.Contains("sku-e3", jordan.AssignedSkuIds);
        Assert.Contains("user-002", h.FakeGraph.Groups["grp-001"].MemberIds);
    }

    [Fact]
    public async Task M8_conditional_access_patches_only_users_enforces_temporary_expiry_and_preserves_permanent_exclusion()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();
        using var admin2 = await h.SignInAsAsync("oid-admin2", "admin2@contoso.com", "Admin Two", [PortalRole.Admin], admin);

        // Add temporary exclusion expiring in 2 hours
        var expiresAt = h.Clock.GetUtcNow().AddHours(2);
        h.FakeGraph.CallLog.Clear();
        using var tempResp = await admin.PostAsJsonAsync("/api/conditional-access/temporary-exclusion",
            new AddTemporaryCaExclusionRequest("ca-001", "user-002", expiresAt, "Business travel", Submit: true));
        Assert.Equal(HttpStatusCode.OK, tempResp.StatusCode);
        var runId = (await tempResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.OK, (await admin2.PostAsJsonAsync($"/api/approvals/{runId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Approved, "Approved travel"))).StatusCode);
        await h.DrainWorkerQueueAsync();

        // Criterion 2: PATCH body contains ONLY conditions.users
        var caPatch = h.FakeGraph.CallLog.Last(c => c.Method == "PATCH" && c.PathAndQuery.Contains("/conditionalAccess/policies/ca-001", StringComparison.Ordinal));
        using (var doc = JsonDocument.Parse(caPatch.Body!))
        {
            Assert.Equal(["conditions"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToList());
            Assert.Equal(["users"], doc.RootElement.GetProperty("conditions").EnumerateObject().Select(p => p.Name).ToList());
        }

        Assert.Contains("user-002", h.FakeGraph.CaPolicies["ca-001"].ExcludeUsers);

        // Criterion 4: Advance clock past expiry -> temporary exclusion automatically removed
        h.Clock.Advance(TimeSpan.FromHours(2) + TimeSpan.FromMinutes(2));
        await h.DrainWorkerQueueAsync();
        Assert.DoesNotContain("user-002", h.FakeGraph.CaPolicies["ca-001"].ExcludeUsers);
    }

    [Fact]
    public async Task M9_and_M10_agent_enrollment_jwt_auth_cert_rotation_csv_export_and_health()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();

        // M9 Criterion 1: Enrollment token works once, wrong key gets 401, revoked agent gets 401
        using var cmdResp = await admin.PostAsJsonAsync("/api/admin/agents/enroll-command", new CreateAgentEnrollmentRequest("DC01 Agent"));
        var enrollCmd = await cmdResp.Content.ReadFromJsonAsync<AgentEnrollmentCommandDto>(RollcallTestHarness.JsonOptions);
        using var ks = AgentKeyStore.CreateNew(h.Clock);
        using var wrongKs = AgentKeyStore.CreateNew(h.Clock);
        using var anon = h.CreateBrowserClient();

        using var enrollResp = await anon.PostAsJsonAsync("/api/agent/enroll",
            new AgentEnrollRequest(enrollCmd!.EnrollmentToken, ks.PublicKeyPem, "DC01.contoso.local", "1.0.0", 1));
        Assert.Equal(HttpStatusCode.OK, enrollResp.StatusCode);

        // Reusing token fails with 401
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/agent/enroll",
            new AgentEnrollRequest(enrollCmd.EnrollmentToken, ks.PublicKeyPem, "DC01", "1.0.0", 1))).StatusCode);

        // Wrong key JWT gets 401
        using (var badHb = new HttpRequestMessage(HttpMethod.Post, "/api/agent/heartbeat"))
        {
            badHb.Headers.Authorization = new AuthenticationHeaderValue("Bearer", wrongKs.CreateSignedJwt(enrollCmd.AgentId));
            badHb.Content = JsonContent.Create(new AgentHeartbeatRequest("DC01", "1.0.0", 1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(badHb)).StatusCode);
        }

        // Valid key JWT succeeds
        using (var okHb = new HttpRequestMessage(HttpMethod.Post, "/api/agent/heartbeat"))
        {
            okHb.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ks.CreateSignedJwt(enrollCmd.AgentId));
            okHb.Content = JsonContent.Create(new AgentHeartbeatRequest("DC01", "1.0.0", 1));
            Assert.Equal(HttpStatusCode.OK, (await anon.SendAsync(okHb)).StatusCode);
        }

        // Revoking agent causes next request to return 401
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/admin/agents/{enrollCmd.AgentId}/revoke", null)).StatusCode);
        using (var revHb = new HttpRequestMessage(HttpMethod.Post, "/api/agent/heartbeat"))
        {
            revHb.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ks.CreateSignedJwt(enrollCmd.AgentId));
            revHb.Content = JsonContent.Create(new AgentHeartbeatRequest("DC01", "1.0.0", 1));
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.SendAsync(revHb)).StatusCode);
        }

        // M10 Criterion 1: Cert expiry warning at 29 days + full zero-downtime cert rotation
        h.Clock.Advance(TimeSpan.FromDays(336));
        using var adminAfterJump = await h.SignInAsAsync("oid-admin", "alex.chen@contoso.com", "Alex Chen");
        var meWarn = await adminAfterJump.GetFromJsonAsync<CurrentUserDto>("/api/me", RollcallTestHarness.JsonOptions);
        Assert.True(meWarn!.ShowCertExpiryWarning);

        var rotStart = await (await adminAfterJump.PostAsync("/api/admin/connections/certificates/rotate/start", null))
            .Content.ReadFromJsonAsync<RotateCertificateStartResponse>(RollcallTestHarness.JsonOptions);
        Assert.NotNull(rotStart);

        var prevConns = await adminAfterJump.GetFromJsonAsync<ConnectionsOverviewDto>("/api/admin/connections", RollcallTestHarness.JsonOptions);
        var oldCertId = prevConns!.ActiveCertificate!.Id;

        Assert.Equal(HttpStatusCode.OK, (await adminAfterJump.PostAsJsonAsync("/api/admin/connections/certificates/rotate/activate",
            new RotateCertificateActionRequest(rotStart.PendingCertificate.Id))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await adminAfterJump.PostAsJsonAsync("/api/admin/connections/certificates/rotate/retire",
            new RotateCertificateActionRequest(oldCertId))).StatusCode);

        var afterConns = await adminAfterJump.GetFromJsonAsync<ConnectionsOverviewDto>("/api/admin/connections", RollcallTestHarness.JsonOptions);
        Assert.Equal(rotStart.PendingCertificate.Id, afterConns!.ActiveCertificate!.Id);
        Assert.False(afterConns.ShowExpiryWarning);

        // M10 Criterion 2: Audit CSV export has UTF-8 BOM and ISO 8601 timestamps
        var csvBytes = await adminAfterJump.GetByteArrayAsync("/api/audit/export");
        Assert.True(csvBytes.Length > 3 && csvBytes[0] == 0xEF && csvBytes[1] == 0xBB && csvBytes[2] == 0xBF);
        var csvText = Encoding.UTF8.GetString(csvBytes, 3, csvBytes.Length - 3);
        Assert.Contains("id,occurred_at,actor", csvText, StringComparison.Ordinal);

        // M10 Criterion 3: Health page reports unhealthy when worker heartbeat missing & permission revoked
        h.FakeGraph.GrantedAppPermissions.Remove("Policy.ReadWrite.ConditionalAccess");
        var health = await adminAfterJump.GetFromJsonAsync<HealthOverviewDto>("/api/admin/health", RollcallTestHarness.JsonOptions);
        Assert.Equal("unhealthy", health!.OverallStatus);
        Assert.Contains(health.Components, c => c.Name == "Background worker" && c.Status == "error");
        Assert.Contains(health.Components, c => c.Name == "Permissions" && c.Status == "error");
    }

    [Fact]
    public async Task M3_transient_429_retry_and_M10_admin_reset_and_master_key_rotation()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();

        // M3 transient Graph 429 Retry-After handling: inject a 429 on /v1.0/users/user-001 PATCH, verify client retries and succeeds
        h.FakeGraph.InjectedFaults["PATCH:/v1.0/users/user-001"] = new Queue<FakeGraphFault>(
            [new FakeGraphFault(HttpStatusCode.TooManyRequests, "Throttled", RetryAfterSeconds: 0)]);

        using var attrResp = await admin.PostAsJsonAsync(
            "/api/users/user-001/attributes",
            new UpdateUserAttributesRequest("Principal Engineer", null, null, null, null, null, null, null, null, null, Submit: true));
        Assert.Equal(HttpStatusCode.OK, attrResp.StatusCode);
        var runId = (await attrResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();

        await h.DrainWorkerQueueAsync();

        var afterRun = await admin.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{runId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.Succeeded, afterRun!.Status);
        Assert.Equal("Principal Engineer", h.FakeGraph.Users["user-001"].JobTitle);

        // M10 admin-reset restores Admin role and writes an audit record
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var cli = scope.ServiceProvider.GetRequiredService<OperationsCliCommands>();
            var exitCode = await cli.AdminResetAsync("recovery.admin@contoso.com");
            Assert.Equal(0, exitCode);

            var db = scope.ServiceProvider.GetRequiredService<RollcallDbContext>();
            var restored = await db.PortalUsers.SingleAsync(u => u.Upn == "recovery.admin@contoso.com");
            Assert.Contains(PortalRole.Admin, restored.GetRoles());

            var auditEntry = await db.AuditEvents.OrderByDescending(e => e.Id)
                .FirstAsync(e => e.Action == "portal.user.admin_reset");
            Assert.Equal("recovery.admin@contoso.com", auditEntry.Target);
        }

        // M10 rotate-master-key re-wraps encrypted app certificates so the new master key decrypts them and the old key fails
        var newMasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await using (var scope = h.Factory.Services.CreateAsyncScope())
        {
            var cli = scope.ServiceProvider.GetRequiredService<OperationsCliCommands>();
            var exitCode = await cli.RotateMasterKeyAsync(RollcallTestHarness.MasterKey, newMasterKey);
            Assert.Equal(0, exitCode);

            var db = scope.ServiceProvider.GetRequiredService<RollcallDbContext>();
            var cert = await db.AppCertificates.FirstAsync();
            Assert.True(cert.KeyVersion >= 2);

            var newConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ROLLCALL_MASTER_KEY"] = newMasterKey })
                .Build();
            var newCrypto = new EnvelopeEncryptionService(newConfig);
            var decrypted = newCrypto.Decrypt(cert.EncryptedPrivateKey);
            Assert.True(decrypted.Length > 0);

            var oldConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ROLLCALL_MASTER_KEY"] = RollcallTestHarness.MasterKey })
                .Build();
            var oldCrypto = new EnvelopeEncryptionService(oldConfig);
            Assert.ThrowsAny<CryptographicException>(() => { _ = oldCrypto.Decrypt(cert.EncryptedPrivateKey); });
        }
    }
}
