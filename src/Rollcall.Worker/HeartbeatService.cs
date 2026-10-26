// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Worker;

/// <summary>
/// Writes a heartbeat row every 15 seconds so the app can report the worker on <c>/readyz</c>,
/// and touches a local file so the container healthcheck can see the last successful beat.
/// </summary>
public sealed partial class HeartbeatService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<HeartbeatService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    internal static string MarkerPath { get; set; } = Path.Combine(Path.GetTempPath(), "rollcall-worker-heartbeat");

    public static string WorkerId { get; } = Environment.MachineName;

    /// <summary>True when this container wrote a heartbeat to the database recently.</summary>
    public static bool IsLocallyHealthy(TimeProvider clock)
    {
        var marker = new FileInfo(MarkerPath);
        return marker.Exists && clock.GetUtcNow() - marker.LastWriteTimeUtc <= ReadinessProbe.HeartbeatMaxAge;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await BeatAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The app may still be migrating, or the database restarting; try again next tick.
                LogBeatFailed(logger, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task BeatAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RollcallDbContext>();
        var now = clock.GetUtcNow();

        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO worker_heartbeat (worker_id, beat_at) VALUES ({WorkerId}, {now})
            ON CONFLICT (worker_id) DO UPDATE SET beat_at = EXCLUDED.beat_at
            """,
            cancellationToken);

        await File.WriteAllTextAsync(MarkerPath, now.ToString("O", System.Globalization.CultureInfo.InvariantCulture), cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't write the worker heartbeat; retrying in 15 seconds: {Reason}")]
    private static partial void LogBeatFailed(ILogger logger, string reason);
}
