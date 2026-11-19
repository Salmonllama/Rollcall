// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Rollcall.Contracts;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Workflow;
using Rollcall.TestSupport;

namespace Rollcall.Api.Tests;

/// <summary>
/// Full-stack test harness wiring WebApplicationFactory to a real per-test PostgreSQL database,
/// FakeGraphHandler, FakeExchangeSessionFactory, FakeOidcTokenIssuer, and injectable FakeTimeProvider.
/// </summary>
public sealed class RollcallTestHarness : IAsyncDisposable
{
    public const string SetupToken = "TESTSETUPTOKEN1234567890ABCDEF";
    public const string MasterKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FakeGraphHandler FakeGraph { get; } = new();
    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    public FakeExchangeSessionFactory FakeExchange { get; }
    public FakeOidcTokenIssuer FakeOidc { get; } = new();
    public string ConnectionString { get; }
    public WebApplicationFactory<Program> Factory { get; }

    private RollcallTestHarness(string connectionString)
    {
        ConnectionString = connectionString;
        FakeExchange = new FakeExchangeSessionFactory(Clock);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Rollcall", connectionString);
            builder.UseSetting("ROLLCALL_SETUP_TOKEN", SetupToken);
            builder.UseSetting("ROLLCALL_MASTER_KEY", MasterKey);
            builder.UseSetting("ROLLCALL_HOSTNAME", "rollcall.contoso.test");
            builder.UseSetting("Graph:BaseUrl", "https://graph.fake.local/v1.0");
            builder.UseSetting("Graph:TokenBaseUrl", "https://login.fake.local");
            builder.UseSetting("Oidc:SkipSignatureValidationForUnitTests", "true");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);

                services.RemoveAll<IExchangeSessionFactory>();
                services.AddSingleton<IExchangeSessionFactory>(FakeExchange);

                services.AddHttpClient("RollcallGraph")
                    .ConfigurePrimaryHttpMessageHandler(() => FakeGraph);
            });
        });
    }

    public static async Task<RollcallTestHarness> CreateAsync(PostgresFixture postgres)
    {
        var cs = await postgres.CreateDatabaseAsync();
        return new RollcallTestHarness(cs);
    }

    public HttpClient CreateBrowserClient() =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });

    /// <summary>Walks the full M2 setup wizard and signs in as Admin.</summary>
    public async Task<HttpClient> CompleteSetupAndSignInAdminAsync()
    {
        var client = CreateBrowserClient();

        using var tokResp = await client.PostAsJsonAsync("/api/setup/verify-token", new VerifySetupTokenRequest(SetupToken));
        Assert.Equal(HttpStatusCode.OK, tokResp.StatusCode);

        using var certResp = await client.PostAsync("/api/setup/certificate", null);
        Assert.Equal(HttpStatusCode.OK, certResp.StatusCode);

        using var valResp = await client.PostAsJsonAsync("/api/setup/validate-connection",
            new ValidateConnectionRequest(FakeGraph.TenantId, "app-client-id-1", FakeGraph.PrimaryDomain));
        Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);

        using var doneResp = await client.PostAsJsonAsync("/api/setup/complete",
            new CompleteSetupRequest("alex.chen@contoso.com", "oid-admin", "Alex Chen"));
        Assert.Equal(HttpStatusCode.OK, doneResp.StatusCode);

        await AttachCsrfHeaderAsync(client);
        return client;
    }

    public async Task<HttpClient> SignInAsAsync(string oid, string upn, string displayName, PortalRole[]? inviteRoles = null, HttpClient? adminClient = null)
    {
        if (inviteRoles is not null && adminClient is not null)
        {
            using var inv = await adminClient.PostAsJsonAsync("/api/admin/team",
                new InvitePortalUserRequest(upn, displayName, oid, inviteRoles));
            Assert.True(inv.IsSuccessStatusCode);
        }

        var client = CreateBrowserClient();
        var idToken = FakeOidc.IssueIdToken(FakeGraph.TenantId, oid, upn, displayName);
        using var resp = await client.PostAsJsonAsync("/api/auth/oidc-token", new { idToken });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await AttachCsrfHeaderAsync(client);
        return client;
    }

    public static async Task AttachCsrfHeaderAsync(HttpClient client)
    {
        var me = await client.GetFromJsonAsync<CurrentUserDto>("/api/me", JsonOptions);
        if (!string.IsNullOrWhiteSpace(me?.CsrfToken))
        {
            client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", me.CsrfToken);
        }
    }

    /// <summary>Executes all queued worker runs synchronously in tests.</summary>
    public async Task DrainWorkerQueueAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();

        await engine.ProcessDueSchedulesAsync();

        while (true)
        {
            var jobs = await queue.LeaseAsync("worker", "test-worker", 10);
            if (jobs.Count == 0)
            {
                break;
            }

            foreach (var job in jobs)
            {
                using var doc = JsonDocument.Parse(job.PayloadJson);
                var runId = doc.RootElement.GetProperty("runId").GetGuid();
                await engine.ExecuteRunAsync(runId);
                await queue.CompleteAsync(job.Id);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        FakeOidc.Dispose();
        FakeGraph.Dispose();
    }
}
