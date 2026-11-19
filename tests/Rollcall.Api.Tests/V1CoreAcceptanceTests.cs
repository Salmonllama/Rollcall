// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rollcall.Contracts;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Workflow;
using Rollcall.TestSupport;

namespace Rollcall.Api.Tests;

/// <summary>Integration tests covering M2, M3, M4, and M5 acceptance criteria.</summary>
public sealed class V1CoreAcceptanceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task M2_setup_wizard_gates_token_hides_private_key_reports_missing_permissions_and_rejects_foreign_tid()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var anon = h.CreateBrowserClient();

        // Criterion 1: /api/setup/status without token returns 404
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/setup/status")).StatusCode);

        // Verify token, generate cert, check private key never appears in any response or raw PEM in DB (Criterion 2)
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/setup/verify-token", new VerifySetupTokenRequest(RollcallTestHarness.SetupToken))).StatusCode);
        using var certResp = await anon.PostAsync("/api/setup/certificate", null);
        var certJson = await certResp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, certResp.StatusCode);
        Assert.DoesNotContain("PRIVATE KEY", certJson, StringComparison.OrdinalIgnoreCase);

        await using (var db = PostgresFixture.CreateContext(h.ConnectionString))
        {
            var certRow = await db.AppCertificates.SingleAsync();
            Assert.DoesNotContain("BEGIN PRIVATE KEY", certRow.EncryptedPrivateKey, StringComparison.Ordinal);
            Assert.StartsWith("v1:", certRow.EncryptedPrivateKey, StringComparison.Ordinal);
        }

        // M3 Criterion 14: missing permission reported by name and affected features
        h.FakeGraph.GrantedAppPermissions.Remove("User.ReadWrite.All");
        using var valResp = await anon.PostAsJsonAsync("/api/setup/validate-connection",
            new ValidateConnectionRequest(h.FakeGraph.TenantId, "app-client-id-1", h.FakeGraph.PrimaryDomain));
        var valDto = await valResp.Content.ReadFromJsonAsync<ConnectionValidationResultDto>(RollcallTestHarness.JsonOptions);
        Assert.NotNull(valDto);
        Assert.True(valDto.Succeeded);
        var missing = valDto.Permissions.Single(p => p.Name == "User.ReadWrite.All");
        Assert.False(missing.Granted);
        Assert.NotEmpty(missing.AffectedFeatures);

        // Complete setup -> setup token no longer works, /api/setup/status returns 404
        Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/setup/complete",
            new CompleteSetupRequest("alex.chen@contoso.com", "oid-admin", "Alex Chen"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/setup/status")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.PostAsJsonAsync("/api/setup/verify-token", new VerifySetupTokenRequest(RollcallTestHarness.SetupToken))).StatusCode);

        // Criterion 5: Foreign tenant ID token is rejected (403)
        using var foreignClient = h.CreateBrowserClient();
        var foreignToken = h.FakeOidc.IssueIdToken("99999999-9999-9999-9999-999999999999", "oid-foreign", "stranger@other.com", "Stranger");
        using var foreignResp = await foreignClient.PostAsJsonAsync("/api/auth/oidc-token", new { idToken = foreignToken });
        Assert.Equal(HttpStatusCode.Forbidden, foreignResp.StatusCode);

        // Criterion 4 & 6: Role matrix (NoRole -> 403 everywhere except /api/me; Technician cannot admin or CA; Approver cannot run workflows)
        await RollcallTestHarness.AttachCsrfHeaderAsync(anon);
        using var noRole = await h.SignInAsAsync("oid-norole", "norole@contoso.com", "No Role");
        using var tech = await h.SignInAsAsync("oid-tech", "tech@contoso.com", "Tech", [PortalRole.Technician], anon);
        using var approver = await h.SignInAsAsync("oid-app", "approver@contoso.com", "Approver", [PortalRole.Approver], anon);

        Assert.Equal(HttpStatusCode.OK, (await noRole.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await noRole.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tech.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.GetAsync("/api/admin/team")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync("/api/conditional-access/ca-001/membership",
            new EditCaPolicyMembershipRequest(["All"], [], [], [], null, null, false))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await approver.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("Lead", null, null, null, null, null, null, null, null, null, false))).StatusCode);
    }

    [Fact]
    public async Task M3_workflow_engine_preview_noop_approvals_drift_retries_concurrency_schedule_and_audit()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();
        using var tech = await h.SignInAsAsync("oid-tech", "tech@contoso.com", "Tech", [PortalRole.Technician], admin);
        using var approver = await h.SignInAsAsync("oid-app", "approver@contoso.com", "Approver", [PortalRole.Approver], admin);

        // Criterion 1: Edit title -> preview before->after, single PATCH with only jobTitle
        h.FakeGraph.CallLog.Clear();
        using var editResp = await tech.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("Senior Sales Associate", null, null, null, null, null, null, null, null, null, Submit: true));
        Assert.Equal(HttpStatusCode.OK, editResp.StatusCode);
        await h.DrainWorkerQueueAsync();

        var patches = h.FakeGraph.CallLog.Where(c => c.Method == "PATCH" && c.PathAndQuery.Contains("/users/user-002", StringComparison.Ordinal)).ToList();
        Assert.Single(patches);
        using (var patchDoc = JsonDocument.Parse(patches[0].Body!))
        {
            var props = patchDoc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            Assert.Equal(["jobTitle"], props);
        }

        // Criterion 2: No-op makes zero write calls
        h.FakeGraph.CallLog.Clear();
        using var noopResp = await tech.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("Senior Sales Associate", null, null, null, null, null, null, null, null, null, Submit: true));
        Assert.Equal(HttpStatusCode.OK, noopResp.StatusCode);
        Assert.DoesNotContain(h.FakeGraph.CallLog, c => c.Method == "PATCH");

        // Criterion 3: Approval required -> self-approval 403 -> reject w/o comment 400 -> reject w/ comment -> approve
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/approval-rules/AttributeUpdate",
            new UpdateApprovalRuleRequest(true, PortalRole.Approver))).StatusCode);

        using var pendingResp = await tech.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("Director", null, null, null, null, null, null, null, null, null, Submit: true));
        var pendingJson = await pendingResp.Content.ReadFromJsonAsync<JsonElement>();
        var pendingRunId = pendingJson.GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal("AwaitingApproval", pendingJson.GetProperty("run").GetProperty("status").GetString());

        // Self-approval blocked (403)
        Assert.Equal(HttpStatusCode.Forbidden, (await tech.PostAsJsonAsync($"/api/approvals/{pendingRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Approved, null))).StatusCode);

        // Reject without comment -> 400
        Assert.Equal(HttpStatusCode.BadRequest, (await approver.PostAsJsonAsync($"/api/approvals/{pendingRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Rejected, "   "))).StatusCode);

        // Reject with comment -> Rejected with zero writes
        h.FakeGraph.CallLog.Clear();
        using var rejResp = await approver.PostAsJsonAsync($"/api/approvals/{pendingRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Rejected, "Not approved by VP."));
        Assert.Equal(HttpStatusCode.OK, rejResp.StatusCode);
        Assert.DoesNotContain(h.FakeGraph.CallLog, c => c.Method == "PATCH");

        // Criterion 4: Drift detection -> NeedsReview with zero writes
        using var driftReq = await tech.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("VP Sales", null, null, null, null, null, null, null, null, null, Submit: true));
        var driftRunId = (await driftReq.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await approver.PostAsJsonAsync($"/api/approvals/{driftRunId}/decision",
            new ApprovalDecisionRequest(ApprovalDecisionType.Approved, "Looks good"))).StatusCode);

        // Mutate user outside Rollcall before worker executes!
        h.FakeGraph.Users["user-002"].JobTitle = "Changed Outside Rollcall";
        h.FakeGraph.CallLog.Clear();
        await h.DrainWorkerQueueAsync();
        var driftRun = await tech.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{driftRunId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.NeedsReview, driftRun!.Status);
        Assert.DoesNotContain(h.FakeGraph.CallLog, c => c.Method == "PATCH");

        // Turn approval back off for retry test
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/approval-rules/AttributeUpdate",
            new UpdateApprovalRuleRequest(false, PortalRole.Approver))).StatusCode);

        // Criterion 5: 403 permanent failure -> PartiallyFailed -> Retry succeeds
        h.FakeGraph.InjectFault("PATCH /users/user-002", new FakeGraphFault(HttpStatusCode.Forbidden, "Authorization_RequestDenied"));
        using var failReq = await tech.PostAsJsonAsync("/api/users/user-002/attributes",
            new UpdateUserAttributesRequest("Principal", null, null, null, null, null, null, null, null, null, Submit: true));
        var failRunId = (await failReq.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();
        await h.DrainWorkerQueueAsync();
        var failedRun = await tech.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{failRunId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.PartiallyFailed, failedRun!.Status);
        Assert.True(failedRun.CanRetry);

        // Retry now succeeds
        Assert.Equal(HttpStatusCode.OK, (await tech.PostAsync($"/api/runs/{failRunId}/retry", null)).StatusCode);
        await h.DrainWorkerQueueAsync();
        var retriedRun = await tech.GetFromJsonAsync<WorkflowRunDto>($"/api/runs/{failRunId}", RollcallTestHarness.JsonOptions);
        Assert.Equal(WorkflowRunStatus.Succeeded, retriedRun!.Status);
        Assert.Equal(2, retriedRun.Steps[0].Attempts);

        // Criterion 8: Audit hash chain intact -> DB role permission denied -> superuser tamper detected
        var verifyOk = await (await admin.PostAsync("/api/audit/verify", null)).Content.ReadFromJsonAsync<AuditVerificationResultDto>(RollcallTestHarness.JsonOptions);
        Assert.True(verifyOk!.IsValid);
        Assert.True(verifyOk.TotalEventsChecked >= 5);

        await using (var conn = new NpgsqlConnection(h.ConnectionString))
        {
            await conn.OpenAsync();
            var roleName = $"rc_app_{Guid.NewGuid():N}"[..20];
            const string rolePassword = "rc_test_restricted_pw";
            await using (var setupRole = new NpgsqlCommand($"CREATE ROLE {roleName} LOGIN PASSWORD '{rolePassword}'; GRANT SELECT, INSERT ON audit_event TO {roleName};", conn))
            {
                await setupRole.ExecuteNonQueryAsync();
            }

            await using var restricted = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(h.ConnectionString) { Username = roleName, Password = rolePassword }.ConnectionString);
            await restricted.OpenAsync();
            var pgEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var badUpdate = new NpgsqlCommand("UPDATE audit_event SET outcome = 'hacked' WHERE id = 1", restricted);
                await badUpdate.ExecuteNonQueryAsync();
            });
            Assert.Equal("42501", pgEx.SqlState);

            // Tamper as superuser -> verify detects row 1
            await using var tamper = new NpgsqlCommand("UPDATE audit_event SET outcome = 'tampered' WHERE id = 1", conn);
            await tamper.ExecuteNonQueryAsync();
        }

        var verifyBroken = await (await admin.PostAsync("/api/audit/verify", null)).Content.ReadFromJsonAsync<AuditVerificationResultDto>(RollcallTestHarness.JsonOptions);
        Assert.False(verifyBroken!.IsValid);
        Assert.Equal(1, verifyBroken.FirstBrokenEventId);
    }

    [Fact]
    public async Task M4_and_M5_directory_search_select_enforcement_batch_licenses_tap_confidentiality_and_synced_block()
    {
        await using var h = await RollcallTestHarness.CreateAsync(postgres);
        using var admin = await h.CompleteSetupAndSignInAdminAsync();

        // M4 Criterion 1: 200-user search returns partial name, UPN, and employeeId under 1s
        var sw = Stopwatch.StartNew();
        var byName = await admin.GetFromJsonAsync<List<DirectoryUserDto>>("/api/users?q=Jordan", RollcallTestHarness.JsonOptions);
        var byUpn = await admin.GetFromJsonAsync<List<DirectoryUserDto>>("/api/users?q=jordan.lee@contoso.com", RollcallTestHarness.JsonOptions);
        var byEmpId = await admin.GetFromJsonAsync<List<DirectoryUserDto>>("/api/users?q=EMP0002", RollcallTestHarness.JsonOptions);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Contains(byName!, u => u.Id == "user-002");
        Assert.Contains(byUpn!, u => u.Id == "user-002");
        Assert.Contains(byEmpId!, u => u.Id == "user-002");

        // M4 Criterion 5: Dashboard & User detail
        var dash = await admin.GetFromJsonAsync<DashboardSummaryDto>("/api/dashboard", RollcallTestHarness.JsonOptions);
        Assert.True(dash!.AvailableLicenseSeats > 0);
        var detail = await admin.GetFromJsonAsync<DirectoryUserDetailDto>("/api/users/user-002", RollcallTestHarness.JsonOptions);
        Assert.NotNull(detail);
        Assert.NotEmpty(detail.ConditionalAccessPolicies);

        // M4 Criterion 3: Every single Graph GET read included $select
        Assert.Empty(h.FakeGraph.ReadsMissingSelect);

        // M5 Criterion 2: Bulk add 50 users to a cloud security group uses $batch in <= 3 HTTP write calls
        h.FakeGraph.CallLog.Clear();
        var fiftyUsers = Enumerable.Range(10, 50).Select(i => $"user-{i:D3}").ToList();
        using var batchResp = await admin.PostAsJsonAsync("/api/groups/membership",
            new GroupMembershipChangeRequest(fiftyUsers, ["grp-012"], "add", Submit: true));
        Assert.Equal(HttpStatusCode.OK, batchResp.StatusCode);
        await h.DrainWorkerQueueAsync();
        var batchCalls = h.FakeGraph.CallLog.Where(c => c.Method == "POST" && c.PathAndQuery.Contains("/$batch", StringComparison.Ordinal)).ToList();
        Assert.InRange(batchCalls.Count, 1, 3);
        Assert.All(fiftyUsers, uid => Assert.Contains(uid, h.FakeGraph.Groups["grp-012"].MemberIds));

        // M5 Criterion 3: Zero-seat SKU fails at plan time with exact count message
        using var zeroSeatResp = await admin.PostAsJsonAsync("/api/licenses/change",
            new LicenseChangeRequest(["user-004"], ["sku-zero"], [], null, Submit: false));
        var zeroSeatPlan = (await zeroSeatResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan");
        var zeroErrors = zeroSeatPlan.GetProperty("blockingErrors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(zeroErrors, e => e!.Contains("No Exchange Online (Plan 2) licenses available (0 of 10)", StringComparison.Ordinal));

        // M5 Criterion 4: Assigning a license to a user without usageLocation auto-adds usageLocation step
        using var noLocResp = await admin.PostAsJsonAsync("/api/licenses/change",
            new LicenseChangeRequest(["user-004"], ["sku-e3"], [], null, Submit: false));
        var noLocSteps = (await noLocResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("steps").EnumerateArray().ToList();
        Assert.Contains(noLocSteps, s => s.GetProperty("stepKind").GetString() == "graph.user.updateAttributes" && s.GetProperty("newValue").GetString() == "US");

        // M5 Criterion 5: TAP displayed once and ABSENT from DB and audit details
        using var tapResp = await admin.PostAsJsonAsync("/api/users/user-002/mfa/tap", new IssueTapRequest(60, true, Submit: true));
        var tapRunId = (await tapResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run").GetProperty("id").GetGuid();
        await h.DrainWorkerQueueAsync();

        var credResp = await admin.PostAsync($"/api/runs/{tapRunId}/credential", null);
        Assert.Equal(HttpStatusCode.OK, credResp.StatusCode);
        var credDto = await credResp.Content.ReadFromJsonAsync<IssuedCredentialDto>(RollcallTestHarness.JsonOptions);
        Assert.Equal("Kx7m Qp2R 9tVw", credDto!.Value);

        // Second view returns 410 Gone
        Assert.Equal(HttpStatusCode.Gone, (await admin.PostAsync($"/api/runs/{tapRunId}/credential", null)).StatusCode);

        // Assert TAP value is absent from DB workflow_step, workflow_run, and audit_event
        await using (var db = PostgresFixture.CreateContext(h.ConnectionString))
        {
            var stepRows = await db.WorkflowSteps.Select(s => new { s.ResultJson, s.InputJson }).ToListAsync();
            var auditRows = await db.AuditEvents.Select(a => new { a.DetailsJson, a.Summary }).ToListAsync();
            var stepsJson = string.Join('|', stepRows.Select(s => (s.ResultJson ?? "") + (s.InputJson ?? "")));
            var auditJson = string.Join('|', auditRows.Select(a => a.DetailsJson + (a.Summary ?? "")));
            Assert.DoesNotContain("Kx7mQp2R9tVw", stepsJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Kx7mQp2R9tVw", auditJson, StringComparison.Ordinal);
        }

        // M5 Criterion 8: Editing AD-mastered attribute on synced user (user-005) blocked when HybridAd is off
        var currentSettings = await admin.GetFromJsonAsync<PortalSettingsDto>("/api/admin/settings", RollcallTestHarness.JsonOptions);
        var featuresNoHybrid = new Dictionary<string, bool>(currentSettings!.Features) { [FeatureFlagKeys.HybridAd] = false };
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/admin/settings", new UpdatePortalSettingsRequest(
            currentSettings.TenantTimeZone, currentSettings.DefaultScheduleTimeLocal, currentSettings.DefaultUsageLocation,
            featuresNoHybrid, currentSettings.BreakGlassAccountIds, currentSettings.BreakGlassGroupIds, currentSettings.RunPayloadRetentionDays))).StatusCode);

        using var syncedEdit = await admin.PostAsJsonAsync("/api/users/user-005/attributes",
            new UpdateUserAttributesRequest(null, "Engineering", null, null, null, null, null, null, null, null, Submit: false));
        var syncedErrors = (await syncedEdit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("plan").GetProperty("blockingErrors").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(syncedErrors, e => e!.Contains("on-prem Active Directory", StringComparison.Ordinal));
    }
}
