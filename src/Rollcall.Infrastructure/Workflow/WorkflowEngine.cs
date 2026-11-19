// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow.Steps;

namespace Rollcall.Infrastructure.Workflow;

public sealed record PlannedStepSpec(string StepKind, string InputJson);

public sealed record ActorIdentity(
    string ObjectId,
    string Upn,
    string DisplayName,
    IReadOnlyList<PortalRole> Roles);

public sealed record PlannedRunResult(
    PlanSummaryDto Plan,
    IReadOnlyList<PlannedStepSpec> Specs,
    bool RequiresApproval,
    PortalRole ApproverRole);

/// <summary>
/// Core Rollcall workflow engine (section 4.4): plans, freezes, approves, checks drift,
/// serializes per target user, executes idempotent steps with automatic transient retries,
/// handles manual retries, and executes scheduled runs and timed reversals.
/// </summary>
public sealed partial class WorkflowEngine(
    RollcallDbContext db,
    IEnumerable<IWorkflowStep> registeredSteps,
    SettingsService settingsService,
    AuditService auditService,
    JobQueueService jobQueue,
    EphemeralCredentialStore credentials,
    IGraphClient graph,
    TimeProvider clock,
    ILogger<WorkflowEngine> logger)
{
    public const int MaxTransientAttempts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InProcessTargetLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IWorkflowStep> _steps =
        registeredSteps.ToDictionary(s => s.Kind, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IWorkflowStep> AllSteps => _steps.Values;

    public async Task<PlannedRunResult> PlanAsync(
        WorkflowRunType runType,
        IReadOnlyList<PlannedStepSpec> specs,
        ActorIdentity actor,
        bool onboardingHasAdminRoles = false,
        IReadOnlyList<string>? extraWarnings = null,
        IReadOnlyList<string>? extraBlockingErrors = null,
        CancellationToken cancellationToken = default)
    {
        var portalSettings = await settingsService.GetPortalSettingsAsync(cancellationToken);
        var (requiresApproval, approverRole) = await settingsService.EvaluateApprovalRequirementAsync(
            runType,
            onboardingHasAdminRoles,
            cancellationToken);

        var planCtx = new PlanContext(
            actor.Upn,
            actor.Roles,
            portalSettings.Features,
            portalSettings.DefaultUsageLocation,
            portalSettings.TenantTimeZone,
            portalSettings.BreakGlassAccountIds,
            portalSettings.BreakGlassGroupIds,
            cancellationToken);

        var stepDtos = new List<StepPlanDto>(specs.Count);
        var allWarnings = new List<string>(extraWarnings ?? []);
        var blocking = new List<string>(extraBlockingErrors ?? []);

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (!_steps.TryGetValue(spec.StepKind, out var step))
            {
                blocking.Add($"Unknown workflow step \"{spec.StepKind}\".");
                continue;
            }

            try
            {
                var plan = await step.PlanUntypedAsync(spec.InputJson, planCtx);
                var stepRequiresApproval = plan.RequiresApproval || (requiresApproval && !plan.IsNoOp);
                if (plan.RequiresApproval)
                {
                    requiresApproval = true;
                }

                var stepWarnings = plan.Warnings ?? [];
                allWarnings.AddRange(stepWarnings);

                stepDtos.Add(new StepPlanDto(
                    Sequence: i + 1,
                    StepKind: spec.StepKind,
                    TargetSystem: plan.TargetSystem,
                    Area: plan.Area,
                    Operation: plan.Operation,
                    Description: plan.Description,
                    ObjectName: plan.ObjectName,
                    OldValue: plan.OldValue,
                    NewValue: plan.NewValue,
                    IsNoOp: plan.IsNoOp,
                    NoOpReason: plan.NoOpReason,
                    RequiresApproval: stepRequiresApproval,
                    Warnings: stepWarnings));
            }
            catch (StepExecutionException ex)
            {
                blocking.Add(ex.PlainLanguageMessage);
            }
        }

        var actionable = stepDtos.Where(s => !s.IsNoOp).ToList();
        var systems = actionable.Select(s => s.TargetSystem).Distinct().OrderBy(s => s).ToList();
        var hash = PlanHasher.ComputeHash(stepDtos);

        var summary = new PlanSummaryDto(
            ActionableChangeCount: actionable.Count,
            NoOpCount: stepDtos.Count - actionable.Count,
            SystemCount: systems.Count,
            ApprovalRequiredCount: actionable.Count(s => s.RequiresApproval),
            Systems: systems,
            Warnings: allWarnings.Distinct(StringComparer.Ordinal).ToList(),
            BlockingErrors: blocking.Distinct(StringComparer.Ordinal).ToList(),
            PlanHash: hash,
            Steps: stepDtos);

        return new PlannedRunResult(summary, specs, requiresApproval && actionable.Count > 0, approverRole);
    }

    public async Task<WorkflowRunEntity> CreateRunAsync(
        WorkflowRunType runType,
        string targetUserRef,
        string? targetUserDisplayName,
        IReadOnlyList<PlannedStepSpec> specs,
        ActorIdentity actor,
        DateTimeOffset? scheduledFor = null,
        object? requestPayload = null,
        Guid? roleTemplateId = null,
        int? roleTemplateVersion = null,
        bool onboardingHasAdminRoles = false,
        IReadOnlyList<string>? extraWarnings = null,
        IReadOnlyList<string>? extraBlockingErrors = null,
        CancellationToken cancellationToken = default)
    {
        var planned = await PlanAsync(
            runType,
            specs,
            actor,
            onboardingHasAdminRoles,
            extraWarnings,
            extraBlockingErrors,
            cancellationToken);

        if (planned.Plan.BlockingErrors.Count > 0)
        {
            throw new StepExecutionException(
                string.Join(" ", planned.Plan.BlockingErrors),
                string.Join("; ", planned.Plan.BlockingErrors));
        }

        var now = clock.GetUtcNow();
        var initialStatus = planned.Plan.ActionableChangeCount == 0
            ? WorkflowRunStatus.Succeeded
            : planned.RequiresApproval
                ? WorkflowRunStatus.AwaitingApproval
                : scheduledFor.HasValue && scheduledFor.Value > now
                    ? WorkflowRunStatus.Scheduled
                    : WorkflowRunStatus.Queued;

        var run = new WorkflowRunEntity
        {
            Id = Guid.NewGuid(),
            Type = runType,
            Status = initialStatus,
            RequestedByUpn = actor.Upn,
            RequestedByDisplayName = actor.DisplayName,
            RequestedByObjectId = actor.ObjectId,
            TargetUserRef = targetUserRef,
            TargetUserDisplayName = targetUserDisplayName,
            CreatedAt = now,
            ScheduledFor = scheduledFor,
            CompletedAt = initialStatus == WorkflowRunStatus.Succeeded ? now : null,
            PlanJson = JsonSerializer.Serialize(planned.Plan, JsonOptions),
            PlanHash = planned.Plan.PlanHash,
            RequestPayloadJson = SecretRedactor.Redact(JsonSerializer.Serialize(requestPayload ?? new { }, JsonOptions)),
            RoleTemplateId = roleTemplateId,
            RoleTemplateVersion = roleTemplateVersion,
            RequiresApproval = planned.RequiresApproval,
        };

        for (var i = 0; i < planned.Plan.Steps.Count; i++)
        {
            var planStep = planned.Plan.Steps[i];
            var spec = specs[i];
            run.Steps.Add(new WorkflowStepEntity
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                Sequence = planStep.Sequence,
                Kind = spec.StepKind,
                TargetSystem = planStep.TargetSystem,
                Area = planStep.Area,
                Description = planStep.Description,
                ObjectName = planStep.ObjectName,
                InputJson = spec.InputJson,
                Status = planStep.IsNoOp ? WorkflowStepStatus.Skipped : WorkflowStepStatus.Pending,
                CompletedAt = planStep.IsNoOp ? now : null,
            });
        }

        db.WorkflowRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        await auditService.AppendAsync(
            actor.Upn,
            actor.DisplayName,
            "run.created",
            targetUserRef,
            initialStatus.ToString(),
            new { run.Id, run.Type, run.PlanHash, planned.Plan.ActionableChangeCount, planned.Plan.NoOpCount, run.ScheduledFor },
            run.Id,
            $"Planned {planned.Plan.ActionableChangeCount} change(s) for {targetUserDisplayName ?? targetUserRef}.",
            cancellationToken);

        if (initialStatus == WorkflowRunStatus.Queued)
        {
            await jobQueue.EnqueueAsync(
                "worker",
                "workflow.execute",
                JsonSerializer.Serialize(new { runId = run.Id }, JsonOptions),
                runId: run.Id,
                cancellationToken: cancellationToken);
        }

        return run;
    }

    public async Task<WorkflowRunEntity> DecideApprovalAsync(
        Guid runId,
        ApprovalDecisionRequest decision,
        ActorIdentity actor,
        CancellationToken cancellationToken = default)
    {
        var run = await db.WorkflowRuns
            .Include(r => r.Steps)
            .Include(r => r.Approvals)
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run {runId} was not found.");

        if (run.Status != WorkflowRunStatus.AwaitingApproval)
        {
            throw new InvalidOperationException($"Run {runId} is in state {run.Status} and is not waiting for approval.");
        }

        var (_, requiredRole) = await settingsService.EvaluateApprovalRequirementAsync(run.Type, false, cancellationToken);
        if (!RoleAuthorizationPolicy.CanApproveRun(actor.Roles, actor.Upn, run.RequestedByUpn, requiredRole))
        {
            throw new UnauthorizedAccessException(
                string.Equals(actor.Upn, run.RequestedByUpn, StringComparison.OrdinalIgnoreCase)
                    ? "You can't approve your own request. Ask another approver to review it."
                    : $"You need the {requiredRole} role to approve this request.");
        }

        if (decision.Decision == ApprovalDecisionType.Rejected && string.IsNullOrWhiteSpace(decision.Comment))
        {
            throw new ArgumentException("A rejection requires a comment explaining why the request was rejected.");
        }

        var now = clock.GetUtcNow();
        var approval = new ApprovalEntity
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            ApproverObjectId = actor.ObjectId,
            ApproverUpn = actor.Upn,
            ApproverDisplayName = actor.DisplayName,
            Decision = decision.Decision,
            Comment = string.IsNullOrWhiteSpace(decision.Comment) ? null : decision.Comment.Trim(),
            DecidedAt = now,
        };
        db.Approvals.Add(approval);
        run.Approvals.Add(approval);

        if (decision.Decision == ApprovalDecisionType.Rejected)
        {
            run.Status = WorkflowRunStatus.Rejected;
            run.CompletedAt = now;
            run.StatusReason = approval.Comment;
            await db.SaveChangesAsync(cancellationToken);

            await auditService.AppendAsync(
                actor.Upn,
                actor.DisplayName,
                "run.rejected",
                run.TargetUserRef,
                "rejected",
                new { run.Id, comment = approval.Comment },
                run.Id,
                $"Rejected by {actor.DisplayName}: {approval.Comment}",
                cancellationToken);
            return run;
        }

        run.Status = run.ScheduledFor.HasValue && run.ScheduledFor.Value > now
            ? WorkflowRunStatus.Scheduled
            : WorkflowRunStatus.Queued;
        await db.SaveChangesAsync(cancellationToken);

        await auditService.AppendAsync(
            actor.Upn,
            actor.DisplayName,
            "run.approved",
            run.TargetUserRef,
            "approved",
            new { run.Id, comment = approval.Comment },
            run.Id,
            $"Approved by {actor.DisplayName}.",
            cancellationToken);

        if (run.Status == WorkflowRunStatus.Queued)
        {
            await jobQueue.EnqueueAsync(
                "worker",
                "workflow.execute",
                JsonSerializer.Serialize(new { runId = run.Id }, JsonOptions),
                runId: run.Id,
                cancellationToken: cancellationToken);
        }

        return run;
    }

    public async Task<WorkflowRunEntity> ExecuteRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var initialRun = await db.WorkflowRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run {runId} was not found.");

        // Serialize two concurrent runs targeting the same user (section 4.4 / M3 criterion 7),
        // while allowing runs for different users to execute in parallel.
        var targetLockKey = initialRun.TargetUserRef.ToLowerInvariant();
        var sem = InProcessTargetLocks.GetOrAdd(targetLockKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(cancellationToken);

        await using var lockConn = new NpgsqlConnection(db.Database.GetConnectionString());
        await lockConn.OpenAsync(cancellationToken);
        var advisoryKey = ComputeAdvisoryLockKey(targetLockKey);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", lockConn))
        {
            lockCmd.Parameters.AddWithValue("key", advisoryKey);
            await lockCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            return await ExecuteRunLockedAsync(runId, cancellationToken);
        }
        finally
        {
            try
            {
                await using var unlockCmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", lockConn);
                unlockCmd.Parameters.AddWithValue("key", advisoryKey);
                await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            finally
            {
                sem.Release();
            }
        }
    }

    private async Task<WorkflowRunEntity> ExecuteRunLockedAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.WorkflowRuns
            .Include(r => r.Steps.OrderBy(s => s.Sequence))
            .Include(r => r.Approvals)
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run {runId} was not found.");

        if (run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Rejected or WorkflowRunStatus.Cancelled)
        {
            return run;
        }

        var orderedSteps = run.Steps.OrderBy(s => s.Sequence).ToList();

        // Drift check before first execution (skip on resume/retry after steps have already succeeded).
        var isFreshStart = orderedSteps.All(s => s.Status is WorkflowStepStatus.Pending or WorkflowStepStatus.Skipped);
        if (isFreshStart)
        {
            var drifted = await DetectDriftAsync(run, orderedSteps, cancellationToken);
            if (drifted)
            {
                run.Status = WorkflowRunStatus.NeedsReview;
                run.StatusReason = "Target state changed since this plan was created. Review the updated preview and re-confirm.";
                await db.SaveChangesAsync(cancellationToken);

                await auditService.AppendAsync(
                    run.RequestedByUpn,
                    run.RequestedByDisplayName,
                    "run.drift_detected",
                    run.TargetUserRef,
                    "needs_review",
                    new { run.Id, run.PlanHash },
                    run.Id,
                    run.StatusReason,
                    cancellationToken);
                return run;
            }
        }

        run.Status = WorkflowRunStatus.Running;
        run.StartedAt ??= clock.GetUtcNow();
        run.StatusReason = null;
        await db.SaveChangesAsync(cancellationToken);

        var correlationId = run.Id.ToString("D");

        foreach (var stepEntity in orderedSteps)
        {
            if (stepEntity.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped)
            {
                continue;
            }

            if (!_steps.TryGetValue(stepEntity.Kind, out var stepImpl))
            {
                stepEntity.Status = WorkflowStepStatus.Failed;
                stepEntity.PlainLanguageError = $"Workflow step \"{stepEntity.Kind}\" is not registered.";
                stepEntity.RawError = stepEntity.PlainLanguageError;
                run.Status = WorkflowRunStatus.PartiallyFailed;
                await db.SaveChangesAsync(cancellationToken);
                return run;
            }

            var succeeded = await ExecuteSingleStepWithRetriesAsync(run, stepEntity, stepImpl, correlationId, cancellationToken);
            if (!succeeded)
            {
                return run;
            }
        }

        run.Status = WorkflowRunStatus.Succeeded;
        run.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        await auditService.AppendAsync(
            run.RequestedByUpn,
            run.RequestedByDisplayName,
            "run.finished",
            run.TargetUserRef,
            "succeeded",
            new { run.Id, run.Type, steps = orderedSteps.Count },
            run.Id,
            $"Completed {run.Type} for {run.TargetUserDisplayName ?? run.TargetUserRef}.",
            cancellationToken);

        return run;
    }

    private async Task<bool> ExecuteSingleStepWithRetriesAsync(
        WorkflowRunEntity run,
        WorkflowStepEntity stepEntity,
        IWorkflowStep stepImpl,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var transientCount = 0;

        while (true)
        {
            stepEntity.Status = WorkflowStepStatus.Running;
            stepEntity.Attempts++;
            stepEntity.StartedAt ??= clock.GetUtcNow();
            stepEntity.PlainLanguageError = null;
            stepEntity.RawError = null;
            await db.SaveChangesAsync(cancellationToken);

            await auditService.AppendAsync(
                run.RequestedByUpn,
                run.RequestedByDisplayName,
                "step.started",
                stepEntity.ObjectName,
                "running",
                new { run.Id, stepId = stepEntity.Id, stepEntity.Kind, stepEntity.Attempts },
                run.Id,
                stepEntity.Description,
                cancellationToken);

            var stepCtx = new StepContext(run.Id, stepEntity.Id, run.RequestedByUpn, correlationId, stepEntity.Attempts, clock, cancellationToken);

            try
            {
                var outcome = await stepImpl.ApplyUntypedAsync(stepEntity.InputJson, stepCtx);
                var verified = await stepImpl.VerifyUntypedAsync(stepEntity.InputJson, outcome.ResultJson, stepCtx);
                if (!verified)
                {
                    throw new StepExecutionException(
                        $"Step \"{stepEntity.Description}\" completed but verification didn't confirm the expected state. Retry this step.",
                        "VerifyAsync returned false.",
                        isTransient: true);
                }

                stepEntity.Status = WorkflowStepStatus.Succeeded;
                stepEntity.ResultJson = SecretRedactor.Redact(outcome.ResultJson);
                stepEntity.UndoDataJson = outcome.Undo is not null ? JsonSerializer.Serialize(outcome.Undo, JsonOptions) : null;
                stepEntity.CompletedAt = clock.GetUtcNow();

                if (!string.IsNullOrWhiteSpace(outcome.EphemeralCredentialValue) && !string.IsNullOrWhiteSpace(outcome.EphemeralCredentialKind))
                {
                    credentials.Store(run.Id, outcome.EphemeralCredentialKind, run.TargetUserRef, outcome.EphemeralCredentialValue, outcome.EphemeralCredentialExpiresAt);
                    run.CredentialKind = outcome.EphemeralCredentialKind;
                    run.CredentialExpiresAt = outcome.EphemeralCredentialExpiresAt;
                    run.CredentialViewed = false;
                }

                await RegisterScheduledReversalsIfNeededAsync(run, stepEntity, outcome.ResultJson, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);

                await auditService.AppendAsync(
                    run.RequestedByUpn,
                    run.RequestedByDisplayName,
                    "step.succeeded",
                    stepEntity.ObjectName,
                    "succeeded",
                    new { run.Id, stepId = stepEntity.Id, stepEntity.Kind, stepEntity.Attempts },
                    run.Id,
                    stepEntity.Description,
                    cancellationToken);
                return true;
            }
            catch (StepExecutionException ex) when (ex.WaitingForAgent)
            {
                stepEntity.Status = WorkflowStepStatus.Waiting;
                stepEntity.WaitStartedAt ??= clock.GetUtcNow();
                stepEntity.PlainLanguageError = ex.PlainLanguageMessage;
                stepEntity.RawError = SecretRedactor.Redact(ex.RawError);
                run.Status = WorkflowRunStatus.WaitingForAgent;
                run.StatusReason = ex.PlainLanguageMessage;
                await db.SaveChangesAsync(cancellationToken);
                return false;
            }
            catch (StepExecutionException ex) when (ex.IsTransient && transientCount < MaxTransientAttempts - 1)
            {
                transientCount++;
                var delay = ex.RetryAfter ?? TimeSpan.FromSeconds(Math.Min(Math.Pow(2, transientCount), 30));
                LogStepTransientRetry(logger, stepEntity.Kind, run.Id, stepEntity.Attempts, delay.TotalSeconds);
                await Task.Delay(delay, clock, cancellationToken);
            }
            catch (Exception ex)
            {
                var stepEx = ex as StepExecutionException;
                stepEntity.Status = WorkflowStepStatus.Failed;
                stepEntity.PlainLanguageError = SecretRedactor.Redact(stepEx?.PlainLanguageMessage ?? $"Step failed: {ex.Message}");
                stepEntity.RawError = SecretRedactor.Redact(stepEx?.RawError ?? ex.ToString());
                stepEntity.GraphRequestId = stepEx?.GraphRequestId;
                stepEntity.GraphClientRequestId = stepEx?.GraphClientRequestId;
                run.Status = WorkflowRunStatus.PartiallyFailed;
                if (stepEntity.Kind == "exchange.mailbox.convertToShared")
                {
                    run.StatusReason = "Mailbox conversion failed, so licenses were kept to prevent data loss. Fix the mailbox error and retry.";
                }
                else
                {
                    run.StatusReason = stepEntity.PlainLanguageError;
                }

                await db.SaveChangesAsync(cancellationToken);

                await auditService.AppendAsync(
                    run.RequestedByUpn,
                    run.RequestedByDisplayName,
                    "step.failed",
                    stepEntity.ObjectName,
                    "failed",
                    new
                    {
                        run.Id,
                        stepId = stepEntity.Id,
                        stepEntity.Kind,
                        stepEntity.Attempts,
                        error = stepEntity.PlainLanguageError,
                        stepEntity.GraphRequestId,
                        stepEntity.GraphClientRequestId,
                    },
                    run.Id,
                    stepEntity.PlainLanguageError,
                    cancellationToken);
                return false;
            }
        }
    }

    public async Task<WorkflowRunEntity> RetryRunAsync(Guid runId, ActorIdentity actor, CancellationToken cancellationToken = default)
    {
        var run = await db.WorkflowRuns
            .Include(r => r.Steps.OrderBy(s => s.Sequence))
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run {runId} was not found.");

        if (run.Status is not (WorkflowRunStatus.PartiallyFailed or WorkflowRunStatus.WaitingForAgent))
        {
            throw new InvalidOperationException($"Run {runId} is {run.Status} and cannot be retried.");
        }

        foreach (var step in run.Steps.Where(s => s.Status is WorkflowStepStatus.Failed or WorkflowStepStatus.Waiting))
        {
            step.Status = WorkflowStepStatus.Pending;
            step.PlainLanguageError = null;
            step.RawError = null;
        }

        run.Status = WorkflowRunStatus.Queued;
        run.StatusReason = null;
        await db.SaveChangesAsync(cancellationToken);

        await auditService.AppendAsync(
            actor.Upn,
            actor.DisplayName,
            "run.retried",
            run.TargetUserRef,
            "queued",
            new { run.Id },
            run.Id,
            $"Retried failed steps by {actor.DisplayName}.",
            cancellationToken);

        await jobQueue.EnqueueAsync(
            "worker",
            "workflow.execute",
            JsonSerializer.Serialize(new { runId = run.Id }, JsonOptions),
            runId: run.Id,
            cancellationToken: cancellationToken);

        return run;
    }

    public async Task<WorkflowRunEntity> CancelRunAsync(Guid runId, ActorIdentity actor, CancellationToken cancellationToken = default)
    {
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            ?? throw new KeyNotFoundException($"Run {runId} was not found.");

        if (run.Status is WorkflowRunStatus.Succeeded or WorkflowRunStatus.Running)
        {
            throw new InvalidOperationException($"Can't cancel a run in state {run.Status}.");
        }

        run.Status = WorkflowRunStatus.Cancelled;
        run.CompletedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);

        await auditService.AppendAsync(
            actor.Upn,
            actor.DisplayName,
            "run.cancelled",
            run.TargetUserRef,
            "cancelled",
            new { run.Id },
            run.Id,
            $"Cancelled by {actor.DisplayName}.",
            cancellationToken);

        return run;
    }

    /// <summary>Promotes due Scheduled runs and executes due ScheduledReversals (M3, M6, M7, M8).</summary>
    public async Task<int> ProcessDueSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var count = 0;

        var dueRuns = await db.WorkflowRuns
            .Where(r => r.Status == WorkflowRunStatus.Scheduled && r.ScheduledFor <= now)
            .ToListAsync(cancellationToken);

        foreach (var run in dueRuns)
        {
            run.Status = WorkflowRunStatus.Queued;
            await db.SaveChangesAsync(cancellationToken);
            await jobQueue.EnqueueAsync(
                "worker",
                "workflow.execute",
                JsonSerializer.Serialize(new { runId = run.Id }, JsonOptions),
                runId: run.Id,
                cancellationToken: cancellationToken);
            count++;
        }

        // Resume any WaitingForAgent runs once an agent is back online (M9 criterion 8).
        var cutoff = now - AgentJobDispatcher.AgentHeartbeatMaxAge;
        var hasConnectedAgent = await db.AdAgents.AsNoTracking()
            .AnyAsync(a => a.Status == AgentStatus.Connected && a.LastSeenAt >= cutoff, cancellationToken);
        if (hasConnectedAgent)
        {
            var waitingRuns = await db.WorkflowRuns
                .Include(r => r.Steps)
                .Where(r => r.Status == WorkflowRunStatus.WaitingForAgent)
                .ToListAsync(cancellationToken);

            foreach (var run in waitingRuns)
            {
                foreach (var s in run.Steps.Where(st => st.Status == WorkflowStepStatus.Waiting))
                {
                    s.Status = WorkflowStepStatus.Pending;
                }

                run.Status = WorkflowRunStatus.Queued;
                run.StatusReason = null;
                await db.SaveChangesAsync(cancellationToken);
                await jobQueue.EnqueueAsync(
                    "worker",
                    "workflow.execute",
                    JsonSerializer.Serialize(new { runId = run.Id }, JsonOptions),
                    runId: run.Id,
                    cancellationToken: cancellationToken);
                count++;
            }
        }

        var dueReversals = await db.ScheduledReversals
            .Where(r => !r.Executed && r.DueAt <= now)
            .ToListAsync(cancellationToken);

        foreach (var reversal in dueReversals)
        {
            await ExecuteScheduledReversalAsync(reversal, cancellationToken);
            count++;
        }

        return count;
    }

    public async Task ExecuteScheduledReversalAsync(ScheduledReversalEntity reversal, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        try
        {
            if (reversal.Kind == "onedrive.revokeAccess")
            {
                var payload = JsonSerializer.Deserialize<OneDriveReversalPayload>(reversal.ActionPayloadJson, JsonOptions)!;
                await graph.RevokeOneDriveAccessAsync(payload.UserId, payload.PermissionId, cancellationToken);
                reversal.Executed = true;
                reversal.ExecutedAt = now;
                reversal.Outcome = $"Removed temporary OneDrive access for {payload.ManagerUpn}.";
                await db.SaveChangesAsync(cancellationToken);

                await auditService.AppendAsync(
                    "system@rollcall",
                    "Rollcall scheduler",
                    "reversal.onedrive.revoked",
                    payload.UserId,
                    "succeeded",
                    new { reversal.Id, reversal.RunId, payload.ManagerUpn, payload.PermissionId },
                    reversal.RunId,
                    reversal.Outcome,
                    cancellationToken);
            }
            else if (reversal.Kind == "ca.removeTemporaryExclusion")
            {
                var payload = JsonSerializer.Deserialize<CaTempExclusionReversalPayload>(reversal.ActionPayloadJson, JsonOptions)!;
                var policy = await graph.GetConditionalAccessPolicyAsync(payload.PolicyId, cancellationToken);
                if (policy is null)
                {
                    reversal.Executed = true;
                    reversal.ExecutedAt = now;
                    reversal.Outcome = "Policy no longer exists; nothing to remove.";
                }
                else if (!string.Equals(policy.ETag, payload.ExpectedPolicyETagAfterGrant, StringComparison.Ordinal) &&
                    policy.ExcludeUsers.Contains(payload.UserId, StringComparer.OrdinalIgnoreCase) &&
                    !await HasOtherActiveReversalForSameTargetAsync(reversal.Id, payload.PolicyId, payload.UserId, cancellationToken))
                {
                    // M8 criterion 4: if someone edited the policy externally and kept/re-added a permanent exclusion,
                    // preserve it and log why.
                    reversal.Executed = true;
                    reversal.ExecutedAt = now;
                    reversal.Outcome = $"Preserved exclusion for {payload.UserId} on \"{policy.DisplayName}\" because the policy was updated outside this temporary exclusion.";
                }
                else
                {
                    var nextExcluded = policy.ExcludeUsers
                        .Where(u => !string.Equals(u, payload.UserId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    await graph.PatchConditionalAccessUsersAsync(
                        policy.Id,
                        policy.IncludeUsers,
                        nextExcluded,
                        policy.IncludeGroups,
                        policy.ExcludeGroups,
                        cancellationToken);
                    reversal.Executed = true;
                    reversal.ExecutedAt = now;
                    reversal.Outcome = $"Removed expired temporary exclusion for {payload.UserId} from \"{policy.DisplayName}\".";
                }

                await db.SaveChangesAsync(cancellationToken);

                await auditService.AppendAsync(
                    "system@rollcall",
                    "Rollcall scheduler",
                    "reversal.ca.expired",
                    payload.PolicyId,
                    "succeeded",
                    new { reversal.Id, reversal.RunId, payload.UserId, outcome = reversal.Outcome },
                    reversal.RunId,
                    reversal.Outcome,
                    cancellationToken);
            }
        }
        catch (Exception ex)
        {
            reversal.Outcome = $"Reversal failed: {SecretRedactor.Redact(ex.Message)}";
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public WorkflowRunDto ToRunDto(WorkflowRunEntity run, ActorIdentity? viewer)
    {
        var plan = JsonSerializer.Deserialize<PlanSummaryDto>(run.PlanJson, JsonOptions)
            ?? new PlanSummaryDto(0, 0, 0, 0, [], [], [], run.PlanHash, []);

        var steps = run.Steps.OrderBy(s => s.Sequence).Select(s =>
        {
            long? duration = s.StartedAt.HasValue && s.CompletedAt.HasValue
                ? (long)(s.CompletedAt.Value - s.StartedAt.Value).TotalMilliseconds
                : null;
            return new WorkflowStepDto(
                s.Id,
                s.Sequence,
                s.Kind,
                s.TargetSystem,
                s.Area,
                s.Description,
                s.ObjectName,
                s.Status,
                s.Attempts,
                s.PlainLanguageError,
                s.RawError,
                s.GraphRequestId,
                s.GraphClientRequestId,
                s.StartedAt,
                s.CompletedAt,
                duration,
                !string.IsNullOrWhiteSpace(s.UndoDataJson));
        }).ToList();

        var approvals = run.Approvals.OrderBy(a => a.DecidedAt).Select(a => new ApprovalDto(
            a.Id,
            a.RunId,
            a.ApproverUpn,
            a.ApproverDisplayName,
            a.Decision,
            a.Comment,
            a.DecidedAt)).ToList();

        var canApprove = viewer is not null &&
            run.Status == WorkflowRunStatus.AwaitingApproval &&
            RoleAuthorizationPolicy.CanApproveRun(
                viewer.Roles,
                viewer.Upn,
                run.RequestedByUpn,
                run.Type == WorkflowRunType.ConditionalAccessEdit ? PortalRole.Admin : PortalRole.Approver);

        var canRetry = viewer is not null &&
            RoleAuthorizationPolicy.CanRunStandardWorkflows(viewer.Roles) &&
            run.Status is WorkflowRunStatus.PartiallyFailed or WorkflowRunStatus.WaitingForAgent;

        var canRestore = viewer is not null &&
            RoleAuthorizationPolicy.CanRunStandardWorkflows(viewer.Roles) &&
            run.Type == WorkflowRunType.Offboarding &&
            run.Status == WorkflowRunStatus.Succeeded &&
            run.Steps.Any(s => !string.IsNullOrWhiteSpace(s.UndoDataJson));

        return new WorkflowRunDto(
            run.Id,
            run.Type,
            run.Status,
            run.RequestedByUpn,
            run.RequestedByDisplayName,
            run.TargetUserRef,
            run.TargetUserDisplayName,
            run.CreatedAt,
            run.ScheduledFor,
            run.StartedAt,
            run.CompletedAt,
            run.PlanHash,
            plan,
            steps,
            approvals,
            run.RoleTemplateId,
            run.RoleTemplateVersion,
            canApprove,
            canRetry,
            canRestore,
            !run.CredentialViewed && credentials.HasCredential(run.Id),
            run.StatusReason);
    }

    public static WorkflowRunListItemDto ToListItemDto(WorkflowRunEntity run)
    {
        var plan = JsonSerializer.Deserialize<PlanSummaryDto>(run.PlanJson, JsonOptions);
        return new WorkflowRunListItemDto(
            run.Id,
            run.Type,
            run.Status,
            run.RequestedByUpn,
            run.RequestedByDisplayName,
            run.TargetUserRef,
            run.TargetUserDisplayName,
            run.CreatedAt,
            run.ScheduledFor,
            run.CompletedAt,
            run.Steps.Count,
            run.Steps.Count(s => s.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped),
            run.Steps.Count(s => s.Status == WorkflowStepStatus.Failed),
            plan?.ActionableChangeCount ?? run.Steps.Count);
    }

    private async Task<bool> DetectDriftAsync(
        WorkflowRunEntity run,
        IReadOnlyList<WorkflowStepEntity> orderedSteps,
        CancellationToken cancellationToken)
    {
        var specs = orderedSteps.Select(s => new PlannedStepSpec(s.Kind, s.InputJson)).ToList();
        var actor = new ActorIdentity(run.RequestedByObjectId, run.RequestedByUpn, run.RequestedByDisplayName, [PortalRole.Admin]);
        var replanned = await PlanAsync(run.Type, specs, actor, cancellationToken: cancellationToken);
        if (replanned.Plan.BlockingErrors.Count > 0)
        {
            return true;
        }

        return !string.Equals(replanned.Plan.PlanHash, run.PlanHash, StringComparison.Ordinal);
    }

    private async Task RegisterScheduledReversalsIfNeededAsync(
        WorkflowRunEntity run,
        WorkflowStepEntity stepEntity,
        string resultJson,
        CancellationToken cancellationToken)
    {
        if (stepEntity.Kind == "graph.onedrive.grantAccess")
        {
            var input = JsonSerializer.Deserialize<GrantOneDriveAccessStepInput>(stepEntity.InputJson, JsonOptions);
            var res = JsonSerializer.Deserialize<GrantOneDriveAccessStepResult>(resultJson, JsonOptions);
            if (input is not null && res is not null && input.RetentionDays > 0)
            {
                db.ScheduledReversals.Add(new ScheduledReversalEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Kind = "onedrive.revokeAccess",
                    TargetRef = res.UserId,
                    Description = $"Remove {res.ManagerUpn}'s temporary OneDrive access for {run.TargetUserRef}",
                    ActionPayloadJson = JsonSerializer.Serialize(new OneDriveReversalPayload(res.UserId, res.ManagerUpn, res.PermissionId), JsonOptions),
                    DueAt = clock.GetUtcNow().AddDays(input.RetentionDays),
                });
            }
        }
        else if (stepEntity.Kind == "graph.ca.updateMembership")
        {
            var input = JsonSerializer.Deserialize<UpdateCaMembershipStepInput>(stepEntity.InputJson, JsonOptions);
            var res = JsonSerializer.Deserialize<UpdateCaMembershipStepResult>(resultJson, JsonOptions);
            if (input is not null && res is not null && input.TemporaryExclusionExpiresAt.HasValue && !string.IsNullOrWhiteSpace(input.TemporaryExcludedUserId))
            {
                db.ScheduledReversals.Add(new ScheduledReversalEntity
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Kind = "ca.removeTemporaryExclusion",
                    TargetRef = input.TemporaryExcludedUserId,
                    Description = $"Remove temporary Conditional Access exclusion on \"{res.PolicyDisplayName}\"",
                    ActionPayloadJson = JsonSerializer.Serialize(
                        new CaTempExclusionReversalPayload(res.PolicyId, input.TemporaryExcludedUserId, res.NewETag),
                        JsonOptions),
                    DueAt = input.TemporaryExclusionExpiresAt.Value,
                });
            }
        }
    }

    private async Task<bool> HasOtherActiveReversalForSameTargetAsync(
        Guid currentId,
        string policyId,
        string userId,
        CancellationToken cancellationToken)
    {
        var others = await db.ScheduledReversals.AsNoTracking()
            .Where(r => r.Id != currentId && !r.Executed && r.Kind == "ca.removeTemporaryExclusion" && r.TargetRef == userId)
            .ToListAsync(cancellationToken);
        return others.Any(o => o.ActionPayloadJson.Contains(policyId, StringComparison.OrdinalIgnoreCase));
    }

    private static long ComputeAdvisoryLockKey(string targetKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("rollcall-target:" + targetKey));
        return BitConverter.ToInt64(hash, 0);
    }

    private sealed record OneDriveReversalPayload(string UserId, string ManagerUpn, string PermissionId);
    private sealed record CaTempExclusionReversalPayload(string PolicyId, string UserId, string ExpectedPolicyETagAfterGrant);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transient failure in step {Kind} for run {RunId} (attempt {Attempt}); retrying in {DelaySeconds:0.#}s.")]
    private static partial void LogStepTransientRetry(ILogger logger, string kind, Guid runId, int attempt, double delaySeconds);
}
