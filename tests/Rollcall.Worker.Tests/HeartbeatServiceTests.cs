// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Rollcall.Infrastructure.Persistence;
using Rollcall.TestSupport;

namespace Rollcall.Worker.Tests;

public sealed class HeartbeatServiceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly string _markerPath = Path.Combine(Path.GetTempPath(), $"rollcall-heartbeat-test-{Guid.NewGuid():N}");

    [Fact]
    public async Task Beat_upserts_one_row_per_worker_and_touches_the_marker()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using (var db = PostgresFixture.CreateContext(connectionString))
        {
            await db.Database.MigrateAsync();
        }

        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-26T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var service = CreateService(connectionString, clock);

        await service.BeatAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(15));
        await service.BeatAsync(CancellationToken.None);

        await using var check = PostgresFixture.CreateContext(connectionString);
        var beat = Assert.Single(await check.WorkerHeartbeats.ToListAsync());
        Assert.Equal(HeartbeatService.WorkerId, beat.WorkerId);
        Assert.Equal(clock.GetUtcNow(), beat.BeatAt);
        Assert.True(File.Exists(_markerPath));
    }

    [Fact]
    public void Healthcheck_fails_without_a_marker_and_when_the_marker_is_old()
    {
        HeartbeatService.MarkerPath = _markerPath;
        Assert.False(HeartbeatService.IsLocallyHealthy(TimeProvider.System));

        File.WriteAllText(_markerPath, "");
        Assert.True(HeartbeatService.IsLocallyHealthy(TimeProvider.System));

        File.SetLastWriteTimeUtc(_markerPath, DateTime.UtcNow.AddMinutes(-1));
        Assert.False(HeartbeatService.IsLocallyHealthy(TimeProvider.System));
    }

    public void Dispose() => File.Delete(_markerPath);

    private HeartbeatService CreateService(string connectionString, TimeProvider clock)
    {
        HeartbeatService.MarkerPath = _markerPath;
        var services = new ServiceCollection()
            .AddDbContext<RollcallDbContext>(options => options.UseNpgsql(connectionString))
            .BuildServiceProvider();
        return new HeartbeatService(services.GetRequiredService<IServiceScopeFactory>(), clock, NullLogger<HeartbeatService>.Instance);
    }
}
