// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Workflow;
using Rollcall.TestSupport;

namespace Rollcall.Worker.Tests;

public sealed class WorkerEngineTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Pooled_exchange_runner_retries_once_on_dead_session_and_passes_confirm_false()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var factory = new FakeExchangeSessionFactory(clock);
        factory.DistributionLists["dl-sales@contoso.com"] = ["jordan.lee@contoso.com"];
        factory.FailNextCommandsWithDeadSession = 1;

        await using var runner = new PooledExchangeRunner(factory, clock, NullLogger<PooledExchangeRunner>.Instance);

        await runner.RemoveDistributionGroupMemberAsync("dl-sales@contoso.com", "jordan.lee@contoso.com");

        Assert.Equal(2, factory.SessionsCreated);
        Assert.Empty(factory.DistributionLists["dl-sales@contoso.com"]);
        var removeCmd = factory.CommandLog.Last(c => c.Cmdlet == "Remove-DistributionGroupMember");
        Assert.Equal("False", removeCmd.Parameters["Confirm"]);
        Assert.Equal("dl-sales@contoso.com", removeCmd.Parameters["Identity"]);
    }

    [Fact]
    public async Task Job_queue_leases_with_skip_locked_and_releases_on_graceful_shutdown()
    {
        var cs = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(cs);
        await new DatabaseMigrator(db, NullLogger<DatabaseMigrator>.Instance).MigrateAsync();

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var queue = new JobQueueService(db, clock);

        var job = await queue.EnqueueAsync("worker", "workflow.execute", "{\"runId\":\"00000000-0000-0000-0000-000000000001\"}");

        var worker1 = await queue.LeaseAsync("worker", "w1", 5);
        Assert.Single(worker1);
        Assert.Equal(job.Id, worker1[0].Id);

        // Second worker sees zero leased jobs while lease is active
        var worker2 = await queue.LeaseAsync("worker", "w2", 5);
        Assert.Empty(worker2);

        // Graceful shutdown releases w1's lease immediately so w2 can pick it up
        await queue.ReleaseLeasesForWorkerAsync("w1");
        var worker2AfterRelease = await queue.LeaseAsync("worker", "w2", 5);
        Assert.Single(worker2AfterRelease);
        Assert.Equal(job.Id, worker2AfterRelease[0].Id);

        // Dead-lettering after max attempts
        await queue.FailAsync(job.Id, "boom", requeue: false);
        var failed = await db.JobQueue.AsNoTracking().FirstOrDefaultAsync(j => j.Id == job.Id);
        Assert.Equal("failed", failed!.Status);
    }
}
