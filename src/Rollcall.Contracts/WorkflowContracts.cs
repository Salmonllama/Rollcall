// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Contracts;

public sealed record StepPlanDto(
    int Sequence,
    string StepKind,
    TargetSystem TargetSystem,
    string Area,
    StepOperation Operation,
    string Description,
    string ObjectName,
    string? OldValue,
    string? NewValue,
    bool IsNoOp,
    string? NoOpReason,
    bool RequiresApproval,
    IReadOnlyList<string> Warnings);

public sealed record PlanSummaryDto(
    int ActionableChangeCount,
    int NoOpCount,
    int SystemCount,
    int ApprovalRequiredCount,
    IReadOnlyList<TargetSystem> Systems,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> BlockingErrors,
    string PlanHash,
    IReadOnlyList<StepPlanDto> Steps);

public sealed record WorkflowStepDto(
    Guid Id,
    int Sequence,
    string Kind,
    TargetSystem TargetSystem,
    string Area,
    string Description,
    string ObjectName,
    WorkflowStepStatus Status,
    int Attempts,
    string? PlainLanguageError,
    string? RawError,
    string? GraphRequestId,
    string? GraphClientRequestId,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    long? DurationMs,
    bool HasUndoData);

public sealed record ApprovalDto(
    Guid Id,
    Guid RunId,
    string ApproverUpn,
    string ApproverDisplayName,
    ApprovalDecisionType Decision,
    string? Comment,
    DateTimeOffset DecidedAt);

public sealed record WorkflowRunDto(
    Guid Id,
    WorkflowRunType Type,
    WorkflowRunStatus Status,
    string RequestedByUpn,
    string RequestedByDisplayName,
    string TargetUserRef,
    string? TargetUserDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string PlanHash,
    PlanSummaryDto Plan,
    IReadOnlyList<WorkflowStepDto> Steps,
    IReadOnlyList<ApprovalDto> Approvals,
    Guid? RoleTemplateId,
    int? RoleTemplateVersion,
    bool CanApprove,
    bool CanRetry,
    bool CanRestore,
    bool HasUnviewedCredential,
    string? StatusReason);

public sealed record WorkflowRunListItemDto(
    Guid Id,
    WorkflowRunType Type,
    WorkflowRunStatus Status,
    string RequestedByUpn,
    string RequestedByDisplayName,
    string TargetUserRef,
    string? TargetUserDisplayName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? CompletedAt,
    int TotalSteps,
    int CompletedSteps,
    int FailedSteps,
    int ActionableChanges);

public sealed record ApprovalRuleDto(
    WorkflowRunType ActionType,
    string Label,
    string Description,
    bool Required,
    PortalRole ApproverRole,
    bool LockedRequired);

public sealed record UpdateApprovalRuleRequest(
    bool Required,
    PortalRole ApproverRole);

public sealed record ApprovalDecisionRequest(
    ApprovalDecisionType Decision,
    string? Comment);

public sealed record AuditEventDto(
    long Id,
    DateTimeOffset OccurredAt,
    string ActorUpn,
    string ActorDisplayName,
    string Action,
    string Target,
    string Outcome,
    Guid? RunId,
    string? Summary,
    string DetailsJson,
    string PrevHash,
    string Hash);

public sealed record AuditVerificationResultDto(
    bool IsValid,
    long TotalEventsChecked,
    long? FirstBrokenEventId,
    string? Message);

public sealed record CursorPage<T>(
    IReadOnlyList<T> Items,
    string? NextCursor,
    int TotalCount);
