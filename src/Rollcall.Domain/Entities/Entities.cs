// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Contracts;

namespace Rollcall.Domain.Entities;

public sealed class TenantEntity
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string AppClientId { get; set; } = string.Empty;
    public string PrimaryDomain { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; set; }
}

public sealed class AppCertificateEntity
{
    public Guid Id { get; set; }
    public string Thumbprint { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Algorithm { get; set; } = "RSA-3072";
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public byte[] PublicCertificateDer { get; set; } = [];
    public string EncryptedPrivateKey { get; set; } = string.Empty;
    public int KeyVersion { get; set; } = 1;
    public AppCertificateStatus Status { get; set; } = AppCertificateStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PortalUserEntity
{
    public Guid Id { get; set; }
    public string EntraObjectId { get; set; } = string.Empty;
    public string Upn { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RolesCsv { get; set; } = string.Empty;
    public bool Disabled { get; set; }
    public DateTimeOffset? LastSignInAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public IReadOnlyList<PortalRole> GetRoles() =>
        string.IsNullOrWhiteSpace(RolesCsv)
            ? []
            : RolesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(r => Enum.TryParse<PortalRole>(r, ignoreCase: true, out var parsed) ? (PortalRole?)parsed : null)
                .Where(r => r.HasValue)
                .Select(r => r!.Value)
                .Distinct()
                .ToArray();

    public void SetRoles(IEnumerable<PortalRole> roles) =>
        RolesCsv = string.Join(',', roles.Distinct().OrderBy(r => r).Select(r => r.ToString()));
}

public sealed class RoleTemplateEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int Version { get; set; } = 1;
    public string DefinitionJson { get; set; } = "{}";
    public bool IsArchived { get; set; }
    public string ETag { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedByUpn { get; set; } = string.Empty;
}

public sealed class ApprovalRuleEntity
{
    public WorkflowRunType ActionType { get; set; }
    public bool Required { get; set; }
    public PortalRole ApproverRole { get; set; } = PortalRole.Approver;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WorkflowRunEntity
{
    public Guid Id { get; set; }
    public WorkflowRunType Type { get; set; }
    public WorkflowRunStatus Status { get; set; }
    public string RequestedByUpn { get; set; } = string.Empty;
    public string RequestedByDisplayName { get; set; } = string.Empty;
    public string RequestedByObjectId { get; set; } = string.Empty;
    public string TargetUserRef { get; set; } = string.Empty;
    public string? TargetUserDisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ScheduledFor { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string PlanJson { get; set; } = "{}";
    public string PlanHash { get; set; } = string.Empty;
    public string RequestPayloadJson { get; set; } = "{}";
    public Guid? RoleTemplateId { get; set; }
    public int? RoleTemplateVersion { get; set; }
    public bool RequiresApproval { get; set; }
    public string? StatusReason { get; set; }
    public bool CredentialViewed { get; set; }
    public string? CredentialKind { get; set; }
    public DateTimeOffset? CredentialExpiresAt { get; set; }
    public List<WorkflowStepEntity> Steps { get; set; } = [];
    public List<ApprovalEntity> Approvals { get; set; } = [];
}

public sealed class WorkflowStepEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public int Sequence { get; set; }
    public string Kind { get; set; } = string.Empty;
    public TargetSystem TargetSystem { get; set; }
    public string Area { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string InputJson { get; set; } = "{}";
    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;
    public int Attempts { get; set; }
    public string? PlainLanguageError { get; set; }
    public string? RawError { get; set; }
    public string? GraphRequestId { get; set; }
    public string? GraphClientRequestId { get; set; }
    public string? ResultJson { get; set; }
    public string? UndoDataJson { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? WaitStartedAt { get; set; }
}

public sealed class ApprovalEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string ApproverObjectId { get; set; } = string.Empty;
    public string ApproverUpn { get; set; } = string.Empty;
    public string ApproverDisplayName { get; set; } = string.Empty;
    public ApprovalDecisionType Decision { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

public sealed class JobQueueItemEntity
{
    public Guid Id { get; set; }
    public string Queue { get; set; } = "worker";
    public string JobKind { get; set; } = string.Empty;
    public Guid? RunId { get; set; }
    public Guid? StepId { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public string? LockedBy { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public string Status { get; set; } = "pending";
    public string? LastError { get; set; }
    public string? ResultJson { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ScheduledReversalEntity
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TargetRef { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ActionPayloadJson { get; set; } = "{}";
    public DateTimeOffset DueAt { get; set; }
    public bool Executed { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
    public string? Outcome { get; set; }
}

public sealed class AuditEventEntity
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string ActorUpn { get; set; } = string.Empty;
    public string ActorDisplayName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string Outcome { get; set; } = "succeeded";
    public Guid? RunId { get; set; }
    public string? Summary { get; set; }
    public string PrevHash { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
}

public sealed class AdAgentEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? EnrollmentTokenHash { get; set; }
    public DateTimeOffset? EnrollmentTokenExpiresAt { get; set; }
    public bool EnrollmentTokenUsed { get; set; }
    public string? PublicKeyPem { get; set; }
    public string? Hostname { get; set; }
    public string? Version { get; set; }
    public int ProtocolVersion { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DirectoryCacheEntity
{
    public string CacheKey { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset FetchedAt { get; set; }
}
