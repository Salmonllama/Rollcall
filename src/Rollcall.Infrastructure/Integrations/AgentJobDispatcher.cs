// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure.Integrations;

public interface IAgentDispatcher
{
    Task<bool> HasConnectedAgentAsync(CancellationToken cancellationToken = default);
    Task<string?> DispatchAndAwaitAsync(
        string jobKind,
        string payloadJson,
        Guid? runId,
        Guid? stepId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Enqueues AD jobs onto job_queue (queue='agent') and awaits the enrolled Windows AD agent's result.
/// If no agent is connected, raises a WaitingForAgent StepExecutionException so the run pauses instead
/// of failing (M9 criterion 8).
/// </summary>
public sealed class AgentJobDispatcher(
    RollcallDbContext db,
    TimeProvider clock) : IAgentDispatcher
{
    public static readonly TimeSpan AgentHeartbeatMaxAge = TimeSpan.FromMinutes(2);

    public async Task<bool> HasConnectedAgentAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - AgentHeartbeatMaxAge;
        return await db.AdAgents.AsNoTracking()
            .AnyAsync(a => a.Status == AgentStatus.Connected && a.LastSeenAt >= cutoff, cancellationToken);
    }

    public async Task<string?> DispatchAndAwaitAsync(
        string jobKind,
        string payloadJson,
        Guid? runId,
        Guid? stepId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var hasAgent = await HasConnectedAgentAsync(cancellationToken);
        if (!hasAgent)
        {
            throw new StepExecutionException(
                "Waiting for an on-prem AD agent to connect. The job will resume automatically once an agent checks in.",
                "No connected AD agent available.",
                isTransient: true,
                waitingForAgent: true);
        }

        // Reuse an existing pending/completed job for this step to keep retries idempotent.
        JobQueueItemEntity? job = null;
        if (stepId.HasValue)
        {
            job = await db.JobQueue
                .Where(j => j.Queue == "agent" && j.StepId == stepId && j.JobKind == jobKind)
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (job is null || job.Status is "failed" or "dead")
        {
            job = new JobQueueItemEntity
            {
                Id = Guid.NewGuid(),
                Queue = "agent",
                JobKind = jobKind,
                RunId = runId,
                StepId = stepId,
                PayloadJson = payloadJson,
                CreatedAt = clock.GetUtcNow(),
                RunAfter = clock.GetUtcNow(),
                Status = "pending",
                MaxAttempts = 3,
            };
            db.JobQueue.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }

        var deadline = clock.GetUtcNow() + timeout;
        while (clock.GetUtcNow() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await db.JobQueue.AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == job.Id, cancellationToken);
            if (current is null)
            {
                throw new StepExecutionException("The AD agent job disappeared from the queue.", $"Missing agent job {job.Id}.");
            }

            if (current.Status == "completed")
            {
                return current.ResultJson;
            }

            if (current.Status is "failed" or "dead")
            {
                var err = current.LastError ?? "The AD agent reported a failure.";
                throw new StepExecutionException(err, err, isTransient: false);
            }

            if (!await HasConnectedAgentAsync(cancellationToken))
            {
                throw new StepExecutionException(
                    "The AD agent went offline while processing this step. The run will resume once the agent reconnects.",
                    "AD agent offline.",
                    isTransient: true,
                    waitingForAgent: true);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new StepExecutionException(
            "Timed out waiting for the AD agent to finish this step. Check Admin › Agents and retry.",
            $"Agent job {job.Id} ({jobKind}) timed out after {timeout.TotalSeconds:0}s.",
            isTransient: true,
            waitingForAgent: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string SerializePayload<T>(T payload) =>
        JsonSerializer.Serialize(payload, JsonOptions);
}
