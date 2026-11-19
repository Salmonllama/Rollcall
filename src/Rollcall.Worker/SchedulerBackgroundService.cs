// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Worker;

/// <summary>
/// Promotes due Scheduled runs, resumes WaitingForAgent runs once an agent reconnects,
/// fires due ScheduledReversals (OneDrive & temporary CA exclusions), and enforces optional
/// run payload retention (never pruning audit_event).
/// </summary>
public sealed partial class SchedulerBackgroundService(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<SchedulerBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSchedulerTickFailed(logger, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        var db = scope.ServiceProvider.GetRequiredService<RollcallDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<SettingsService>();

        await engine.ProcessDueSchedulesAsync(cancellationToken);

        // Mark stale agents offline
        var staleCutoff = clock.GetUtcNow().AddMinutes(-2);
        var staleAgents = await db.AdAgents
            .Where(a => a.Status == AgentStatus.Connected && a.LastSeenAt < staleCutoff)
            .ToListAsync(cancellationToken);
        if (staleAgents.Count > 0)
        {
            foreach (var agent in staleAgents)
            {
                agent.Status = AgentStatus.Offline;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        // Optional run payload retention (audit_event is never pruned by the app).
        var portalSettings = await settings.GetPortalSettingsAsync(cancellationToken);
        if (portalSettings.RunPayloadRetentionDays is > 0 and var days)
        {
            var pruneCutoff = clock.GetUtcNow().AddDays(-days);
            var oldRuns = await db.WorkflowRuns
                .Where(r => r.CompletedAt != null && r.CompletedAt < pruneCutoff && r.RequestPayloadJson != "{\"pruned\":true}")
                .Take(50)
                .ToListAsync(cancellationToken);
            if (oldRuns.Count > 0)
            {
                foreach (var run in oldRuns)
                {
                    run.RequestPayloadJson = "{\"pruned\":true}";
                }

                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduler tick failed: {Reason}")]
    private static partial void LogSchedulerTickFailed(ILogger logger, string reason);
}
