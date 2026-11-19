// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Rollcall.Infrastructure.Persistence;
using Rollcall.TestSupport;

namespace Rollcall.Api.Tests;

public sealed class HealthEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private sealed record Readiness(string Db, string Migrations, string Worker);

    [Fact]
    public async Task Healthz_answers_once_the_app_has_started()
    {
        await using var factory = CreateFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Startup_applies_every_migration()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        using var client = factory.CreateClient();

        await using var db = PostgresFixture.CreateContext(connectionString);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Readyz_is_unavailable_until_the_worker_has_beaten()
    {
        await using var factory = CreateFactory(await postgres.CreateDatabaseAsync());
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(new Readiness("ok", "current", "missing"), await response.Content.ReadFromJsonAsync<Readiness>());
    }

    [Fact]
    public async Task Readyz_reports_ok_with_a_fresh_worker_heartbeat()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        using var client = factory.CreateClient();
        await InsertHeartbeatAsync(connectionString, DateTimeOffset.UtcNow);

        using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"db":"ok","migrations":"current","worker":"ok"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Readyz_reports_a_stale_worker()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var factory = CreateFactory(connectionString);
        using var client = factory.CreateClient();
        await InsertHeartbeatAsync(connectionString, DateTimeOffset.UtcNow.AddMinutes(-2));

        using var response = await client.GetAsync(new Uri("/readyz", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("stale", (await response.Content.ReadFromJsonAsync<Readiness>())!.Worker);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            host.UseSetting("ConnectionStrings:Rollcall", connectionString));

    private static async Task InsertHeartbeatAsync(string connectionString, DateTimeOffset beatAt)
    {
        await using var db = PostgresFixture.CreateContext(connectionString);
        db.WorkerHeartbeats.Add(new WorkerHeartbeat { WorkerId = "test-worker", BeatAt = beatAt });
        await db.SaveChangesAsync();
    }
}
