// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Worker;

/// <summary>
/// Leases and executes queued workflow runs with configurable concurrency (default 4)
/// and graceful lease release on SIGTERM (section 4.4, M3).
/// </summary>
public sealed partial class WorkflowWorkerService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<WorkflowWorkerService> logger) : BackgroundService
{
    private readonly int _concurrency = Math.Clamp(configuration.GetValue("Worker:Concurrency", 4), 1, 16);
    private readonly string _workerId = $"{Environment.MachineName}-{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var activeTasks = new List<Task>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                activeTasks.RemoveAll(t => t.IsCompleted);
                var capacity = _concurrency - activeTasks.Count;
                if (capacity > 0)
                {
                    var leased = await LeaseJobsAsync(capacity, stoppingToken);
                    foreach (var job in leased)
                    {
                        activeTasks.Add(ProcessJobAsync(job.Id, job.JobKind, job.PayloadJson, stoppingToken));
                    }

                    if (leased.Count > 0)
                    {
                        continue;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        finally
        {
            if (activeTasks.Count > 0)
            {
                var drainTimeout = Task.Delay(TimeSpan.FromSeconds(25), CancellationToken.None);
                await Task.WhenAny(Task.WhenAll(activeTasks), drainTimeout);
            }

            await ReleaseRemainingLeasesAsync();
        }
    }

    internal async Task<int> PollOnceAsync(CancellationToken cancellationToken)
    {
        var leased = await LeaseJobsAsync(_concurrency, cancellationToken);
        var tasks = leased.Select(j => ProcessJobAsync(j.Id, j.JobKind, j.PayloadJson, cancellationToken)).ToList();
        await Task.WhenAll(tasks);
        return leased.Count;
    }

    private async Task<IReadOnlyList<Domain.Entities.JobQueueItemEntity>> LeaseJobsAsync(int maxJobs, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        return await queue.LeaseAsync("worker", _workerId, maxJobs, cancellationToken: cancellationToken);
    }

    private async Task ProcessJobAsync(Guid jobId, string jobKind, string payloadJson, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();

        try
        {
            if (jobKind == "workflow.execute")
            {
                using var doc = JsonDocument.Parse(payloadJson);
                var runId = doc.RootElement.GetProperty("runId").GetGuid();
                await engine.ExecuteRunAsync(runId, cancellationToken);
            }

            await queue.CompleteAsync(jobId, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Lease will be released by ReleaseRemainingLeasesAsync.
        }
        catch (Exception ex)
        {
            LogJobFailed(logger, jobId, jobKind, ex.Message);
            await queue.FailAsync(jobId, ex.Message, requeue: true, cancellationToken: CancellationToken.None);
        }
    }

    private async Task ReleaseRemainingLeasesAsync()
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var queue = scope.ServiceProvider.GetRequiredService<JobQueueService>();
            await queue.ReleaseLeasesForWorkerAsync(_workerId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            LogReleaseFailed(logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Worker job {JobId} ({JobKind}) failed: {Reason}")]
    private static partial void LogJobFailed(ILogger logger, Guid jobId, string jobKind, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't release remaining job leases during shutdown: {Reason}")]
    private static partial void LogReleaseFailed(ILogger logger, string reason);
}
