// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure.Health;

/// <summary>Result of a readiness check. Each field is "ok"/"current" when healthy.</summary>
public sealed record ReadinessReport(string Db, string Migrations, string Worker)
{
    public bool IsReady => Db == "ok" && Migrations == "current" && Worker == "ok";
}

/// <summary>Checks the database, the migration state, and the worker heartbeat.</summary>
public sealed class ReadinessProbe(RollcallDbContext db, TimeProvider clock)
{
    /// <summary>The worker beats every 15 seconds; three missed beats means it is down.</summary>
    public static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromSeconds(45);

    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!await db.Database.CanConnectAsync(cancellationToken))
        {
            return new ReadinessReport("unreachable", "unknown", "unknown");
        }

        var pending = await db.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
        {
            return new ReadinessReport("ok", "pending", "unknown");
        }

        var lastBeat = await db.WorkerHeartbeats
            .OrderByDescending(h => h.BeatAt)
            .Select(h => (DateTimeOffset?)h.BeatAt)
            .FirstOrDefaultAsync(cancellationToken);

        var worker = lastBeat switch
        {
            null => "missing",
            var beat when clock.GetUtcNow() - beat.Value > HeartbeatMaxAge => "stale",
            _ => "ok",
        };

        return new ReadinessReport("ok", "current", worker);
    }
}
