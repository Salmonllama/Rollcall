// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rollcall.Domain.Entities;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure.Workflow;

/// <summary>
/// Built-in PostgreSQL job queue using SELECT ... FOR UPDATE SKIP LOCKED with visibility leases,
/// dead-lettering, and graceful lease release on shutdown (sections 2, 4.3, M3).
/// </summary>
public sealed class JobQueueService(RollcallDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<JobQueueItemEntity> EnqueueAsync(
        string queue,
        string jobKind,
        string payloadJson,
        Guid? runId = null,
        Guid? stepId = null,
        DateTimeOffset? runAfter = null,
        int maxAttempts = 5,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var item = new JobQueueItemEntity
        {
            Id = Guid.NewGuid(),
            Queue = queue,
            JobKind = jobKind,
            RunId = runId,
            StepId = stepId,
            PayloadJson = payloadJson,
            CreatedAt = now,
            RunAfter = runAfter ?? now,
            Status = "pending",
            MaxAttempts = maxAttempts,
        };
        db.JobQueue.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    /// <summary>Atomically leases up to <paramref name="maxJobs"/> due jobs using FOR UPDATE SKIP LOCKED.</summary>
    public async Task<IReadOnlyList<JobQueueItemEntity>> LeaseAsync(
        string queue,
        string workerId,
        int maxJobs,
        TimeSpan? leaseDuration = null,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + (leaseDuration ?? DefaultLeaseDuration);
        var limit = Math.Clamp(maxJobs, 1, 20);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var leasedIds = await db.Database
            .SqlQueryRaw<Guid>(
                """
                WITH next_jobs AS (
                    SELECT id
                    FROM job_queue
                    WHERE queue = @queue
                      AND (
                            (status = 'pending' AND run_after <= @now)
                         OR (status = 'leased' AND locked_until <= @now)
                      )
                    ORDER BY run_after ASC, created_at ASC
                    FOR UPDATE SKIP LOCKED
                    LIMIT @limit
                )
                UPDATE job_queue j
                SET status = 'leased',
                    locked_by = @workerId,
                    locked_until = @leaseUntil,
                    attempts = j.attempts + 1
                FROM next_jobs
                WHERE j.id = next_jobs.id
                RETURNING j.id
                """,
                new NpgsqlParameter("queue", queue),
                new NpgsqlParameter("now", now),
                new NpgsqlParameter("limit", limit),
                new NpgsqlParameter("workerId", workerId),
                new NpgsqlParameter("leaseUntil", leaseUntil))
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        if (leasedIds.Count == 0)
        {
            return [];
        }

        return await db.JobQueue.AsNoTracking()
            .Where(j => leasedIds.Contains(j.Id))
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task CompleteAsync(Guid jobId, string? resultJson = null, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        await db.JobQueue
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "completed")
                    .SetProperty(j => j.ResultJson, resultJson)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.CompletedAt, now),
                cancellationToken);
    }

    public async Task FailAsync(
        Guid jobId,
        string error,
        bool requeue,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        var job = await db.JobQueue.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var shouldRequeue = requeue && job.Attempts < job.MaxAttempts;
        var nextStatus = shouldRequeue ? "pending" : (job.Attempts >= job.MaxAttempts ? "dead" : "failed");
        var nextRunAfter = shouldRequeue ? now + (delay ?? TimeSpan.FromSeconds(Math.Pow(2, job.Attempts))) : job.RunAfter;
        DateTimeOffset? completedAt = shouldRequeue ? null : now;

        await db.JobQueue
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, nextStatus)
                    .SetProperty(j => j.LastError, error)
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.RunAfter, nextRunAfter)
                    .SetProperty(j => j.CompletedAt, completedAt),
                cancellationToken);
    }

    public async Task ReleaseLeasesForWorkerAsync(string workerId, CancellationToken cancellationToken = default)
    {
        await db.JobQueue
            .Where(j => j.Status == "leased" && j.LockedBy == workerId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, "pending")
                    .SetProperty(j => j.LockedBy, (string?)null)
                    .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(j => j.Attempts, j => j.Attempts > 0 ? j.Attempts - 1 : 0),
                cancellationToken);
    }

    public async Task<int> GetPendingDepthAsync(CancellationToken cancellationToken = default) =>
        await db.JobQueue.CountAsync(j => j.Status == "pending" || j.Status == "leased", cancellationToken);
}
