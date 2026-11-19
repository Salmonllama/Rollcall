// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Rollcall.Domain.Entities;

namespace Rollcall.Infrastructure.Persistence;

public sealed class RollcallDbContext(DbContextOptions<RollcallDbContext> options) : DbContext(options)
{
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<AppCertificateEntity> AppCertificates => Set<AppCertificateEntity>();
    public DbSet<PortalUserEntity> PortalUsers => Set<PortalUserEntity>();
    public DbSet<RoleTemplateEntity> RoleTemplates => Set<RoleTemplateEntity>();
    public DbSet<ApprovalRuleEntity> ApprovalRules => Set<ApprovalRuleEntity>();
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();
    public DbSet<WorkflowStepEntity> WorkflowSteps => Set<WorkflowStepEntity>();
    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();
    public DbSet<JobQueueItemEntity> JobQueue => Set<JobQueueItemEntity>();
    public DbSet<ScheduledReversalEntity> ScheduledReversals => Set<ScheduledReversalEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<AdAgentEntity> AdAgents => Set<AdAgentEntity>();
    public DbSet<AppSettingEntity> Settings => Set<AppSettingEntity>();
    public DbSet<DirectoryCacheEntity> DirectoryCache => Set<DirectoryCacheEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkerHeartbeat>(entity =>
        {
            entity.ToTable("worker_heartbeat");
            entity.HasKey(h => h.WorkerId);
            entity.Property(h => h.WorkerId).HasColumnName("worker_id").HasMaxLength(200);
            entity.Property(h => h.BeatAt).HasColumnName("beat_at");
        });

        modelBuilder.Entity<TenantEntity>(entity =>
        {
            entity.ToTable("tenant");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id");
            entity.Property(t => t.TenantId).HasColumnName("tenant_id").HasMaxLength(128).IsRequired();
            entity.Property(t => t.AppClientId).HasColumnName("app_client_id").HasMaxLength(128).IsRequired();
            entity.Property(t => t.PrimaryDomain).HasColumnName("primary_domain").HasMaxLength(256).IsRequired();
            entity.Property(t => t.OrganizationName).HasColumnName("organization_name").HasMaxLength(256).IsRequired();
            entity.Property(t => t.ConnectedAt).HasColumnName("connected_at");
        });

        modelBuilder.Entity<AppCertificateEntity>(entity =>
        {
            entity.ToTable("app_certificate");
            entity.HasKey(c => c.Id);
            entity.Property(c => c.Id).HasColumnName("id");
            entity.Property(c => c.Thumbprint).HasColumnName("thumbprint").HasMaxLength(128).IsRequired();
            entity.Property(c => c.Subject).HasColumnName("subject").HasMaxLength(256).IsRequired();
            entity.Property(c => c.Algorithm).HasColumnName("algorithm").HasMaxLength(64).IsRequired();
            entity.Property(c => c.NotBefore).HasColumnName("not_before");
            entity.Property(c => c.NotAfter).HasColumnName("not_after");
            entity.Property(c => c.PublicCertificateDer).HasColumnName("public_certificate_der").IsRequired();
            entity.Property(c => c.EncryptedPrivateKey).HasColumnName("encrypted_private_key").IsRequired();
            entity.Property(c => c.KeyVersion).HasColumnName("key_version");
            entity.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(c => c.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(c => c.Thumbprint).IsUnique();
        });

        modelBuilder.Entity<PortalUserEntity>(entity =>
        {
            entity.ToTable("portal_user");
            entity.HasKey(u => u.Id);
            entity.Property(u => u.Id).HasColumnName("id");
            entity.Property(u => u.EntraObjectId).HasColumnName("entra_object_id").HasMaxLength(128).IsRequired();
            entity.Property(u => u.Upn).HasColumnName("upn").HasMaxLength(320).IsRequired();
            entity.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(256).IsRequired();
            entity.Property(u => u.RolesCsv).HasColumnName("roles").HasMaxLength(256).IsRequired();
            entity.Property(u => u.Disabled).HasColumnName("disabled");
            entity.Property(u => u.LastSignInAt).HasColumnName("last_sign_in_at");
            entity.Property(u => u.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(u => u.Upn).IsUnique();
            entity.HasIndex(u => u.EntraObjectId);
        });

        modelBuilder.Entity<RoleTemplateEntity>(entity =>
        {
            entity.ToTable("role_template");
            entity.HasKey(t => t.Id);
            entity.Property(t => t.Id).HasColumnName("id");
            entity.Property(t => t.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(t => t.Description).HasColumnName("description").HasMaxLength(1000);
            entity.Property(t => t.Version).HasColumnName("version");
            entity.Property(t => t.DefinitionJson).HasColumnName("definition").HasColumnType("jsonb").IsRequired();
            entity.Property(t => t.IsArchived).HasColumnName("is_archived");
            entity.Property(t => t.ETag).HasColumnName("etag").HasMaxLength(128).IsRequired();
            entity.Property(t => t.CreatedAt).HasColumnName("created_at");
            entity.Property(t => t.UpdatedAt).HasColumnName("updated_at");
            entity.Property(t => t.UpdatedByUpn).HasColumnName("updated_by_upn").HasMaxLength(320).IsRequired();
            entity.HasIndex(t => t.Name);
        });

        modelBuilder.Entity<ApprovalRuleEntity>(entity =>
        {
            entity.ToTable("approval_rule");
            entity.HasKey(r => r.ActionType);
            entity.Property(r => r.ActionType).HasColumnName("action_type").HasConversion<string>().HasMaxLength(64);
            entity.Property(r => r.Required).HasColumnName("required");
            entity.Property(r => r.ApproverRole).HasColumnName("approver_role").HasConversion<string>().HasMaxLength(32);
            entity.Property(r => r.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<WorkflowRunEntity>(entity =>
        {
            entity.ToTable("workflow_run");
            entity.HasKey(r => r.Id);
            entity.Property(r => r.Id).HasColumnName("id");
            entity.Property(r => r.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(64);
            entity.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(64);
            entity.Property(r => r.RequestedByUpn).HasColumnName("requested_by_upn").HasMaxLength(320).IsRequired();
            entity.Property(r => r.RequestedByDisplayName).HasColumnName("requested_by_display_name").HasMaxLength(256).IsRequired();
            entity.Property(r => r.RequestedByObjectId).HasColumnName("requested_by_object_id").HasMaxLength(128).IsRequired();
            entity.Property(r => r.TargetUserRef).HasColumnName("target_user_ref").HasMaxLength(320).IsRequired();
            entity.Property(r => r.TargetUserDisplayName).HasColumnName("target_user_display_name").HasMaxLength(256);
            entity.Property(r => r.CreatedAt).HasColumnName("created_at");
            entity.Property(r => r.ScheduledFor).HasColumnName("scheduled_for");
            entity.Property(r => r.StartedAt).HasColumnName("started_at");
            entity.Property(r => r.CompletedAt).HasColumnName("completed_at");
            entity.Property(r => r.PlanJson).HasColumnName("plan").HasColumnType("jsonb").IsRequired();
            entity.Property(r => r.PlanHash).HasColumnName("plan_hash").HasMaxLength(128).IsRequired();
            entity.Property(r => r.RequestPayloadJson).HasColumnName("request_payload").HasColumnType("jsonb").IsRequired();
            entity.Property(r => r.RoleTemplateId).HasColumnName("role_template_id");
            entity.Property(r => r.RoleTemplateVersion).HasColumnName("role_template_version");
            entity.Property(r => r.RequiresApproval).HasColumnName("requires_approval");
            entity.Property(r => r.StatusReason).HasColumnName("status_reason").HasMaxLength(1000);
            entity.Property(r => r.CredentialViewed).HasColumnName("credential_viewed");
            entity.Property(r => r.CredentialKind).HasColumnName("credential_kind").HasMaxLength(64);
            entity.Property(r => r.CredentialExpiresAt).HasColumnName("credential_expires_at");
            entity.HasMany(r => r.Steps).WithOne().HasForeignKey(s => s.RunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(r => r.Approvals).WithOne().HasForeignKey(a => a.RunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(r => r.Status);
            entity.HasIndex(r => r.ScheduledFor);
            entity.HasIndex(r => r.TargetUserRef);
            entity.HasIndex(r => r.CreatedAt);
        });

        modelBuilder.Entity<WorkflowStepEntity>(entity =>
        {
            entity.ToTable("workflow_step");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id");
            entity.Property(s => s.RunId).HasColumnName("run_id");
            entity.Property(s => s.Sequence).HasColumnName("seq");
            entity.Property(s => s.Kind).HasColumnName("kind").HasMaxLength(128).IsRequired();
            entity.Property(s => s.TargetSystem).HasColumnName("target_system").HasConversion<string>().HasMaxLength(64);
            entity.Property(s => s.Area).HasColumnName("area").HasMaxLength(64).IsRequired();
            entity.Property(s => s.Description).HasColumnName("description").HasMaxLength(1000).IsRequired();
            entity.Property(s => s.ObjectName).HasColumnName("object_name").HasMaxLength(512).IsRequired();
            entity.Property(s => s.InputJson).HasColumnName("input").HasColumnType("jsonb").IsRequired();
            entity.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(s => s.Attempts).HasColumnName("attempts");
            entity.Property(s => s.PlainLanguageError).HasColumnName("last_error").HasMaxLength(2000);
            entity.Property(s => s.RawError).HasColumnName("raw_error");
            entity.Property(s => s.GraphRequestId).HasColumnName("graph_request_id").HasMaxLength(128);
            entity.Property(s => s.GraphClientRequestId).HasColumnName("graph_client_request_id").HasMaxLength(128);
            entity.Property(s => s.ResultJson).HasColumnName("result").HasColumnType("jsonb");
            entity.Property(s => s.UndoDataJson).HasColumnName("undo_data").HasColumnType("jsonb");
            entity.Property(s => s.StartedAt).HasColumnName("started_at");
            entity.Property(s => s.CompletedAt).HasColumnName("completed_at");
            entity.Property(s => s.WaitStartedAt).HasColumnName("wait_started_at");
            entity.HasIndex(s => new { s.RunId, s.Sequence }).IsUnique();
        });

        modelBuilder.Entity<ApprovalEntity>(entity =>
        {
            entity.ToTable("approval");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id");
            entity.Property(a => a.RunId).HasColumnName("run_id");
            entity.Property(a => a.ApproverObjectId).HasColumnName("approver_object_id").HasMaxLength(128).IsRequired();
            entity.Property(a => a.ApproverUpn).HasColumnName("approver_upn").HasMaxLength(320).IsRequired();
            entity.Property(a => a.ApproverDisplayName).HasColumnName("approver_display_name").HasMaxLength(256).IsRequired();
            entity.Property(a => a.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(32);
            entity.Property(a => a.Comment).HasColumnName("comment").HasMaxLength(2000);
            entity.Property(a => a.DecidedAt).HasColumnName("decided_at");
            entity.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<JobQueueItemEntity>(entity =>
        {
            entity.ToTable("job_queue");
            entity.HasKey(j => j.Id);
            entity.Property(j => j.Id).HasColumnName("id");
            entity.Property(j => j.Queue).HasColumnName("queue").HasMaxLength(32).IsRequired();
            entity.Property(j => j.JobKind).HasColumnName("job_kind").HasMaxLength(128).IsRequired();
            entity.Property(j => j.RunId).HasColumnName("run_id");
            entity.Property(j => j.StepId).HasColumnName("step_id");
            entity.Property(j => j.PayloadJson).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            entity.Property(j => j.CreatedAt).HasColumnName("created_at");
            entity.Property(j => j.RunAfter).HasColumnName("run_after");
            entity.Property(j => j.LockedBy).HasColumnName("locked_by").HasMaxLength(200);
            entity.Property(j => j.LockedUntil).HasColumnName("locked_until");
            entity.Property(j => j.Attempts).HasColumnName("attempts");
            entity.Property(j => j.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(j => j.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
            entity.Property(j => j.LastError).HasColumnName("last_error");
            entity.Property(j => j.ResultJson).HasColumnName("result").HasColumnType("jsonb");
            entity.Property(j => j.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(j => new { j.Queue, j.Status, j.RunAfter });
        });

        modelBuilder.Entity<ScheduledReversalEntity>(entity =>
        {
            entity.ToTable("scheduled_reversal");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).HasColumnName("id");
            entity.Property(s => s.RunId).HasColumnName("run_id");
            entity.Property(s => s.Kind).HasColumnName("kind").HasMaxLength(128).IsRequired();
            entity.Property(s => s.TargetRef).HasColumnName("target_ref").HasMaxLength(320).IsRequired();
            entity.Property(s => s.Description).HasColumnName("description").HasMaxLength(1000).IsRequired();
            entity.Property(s => s.ActionPayloadJson).HasColumnName("action").HasColumnType("jsonb").IsRequired();
            entity.Property(s => s.DueAt).HasColumnName("due_at");
            entity.Property(s => s.Executed).HasColumnName("executed");
            entity.Property(s => s.ExecutedAt).HasColumnName("executed_at");
            entity.Property(s => s.Outcome).HasColumnName("outcome").HasMaxLength(1000);
            entity.HasIndex(s => new { s.Executed, s.DueAt });
        });

        modelBuilder.Entity<AuditEventEntity>(entity =>
        {
            entity.ToTable("audit_event");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            entity.Property(a => a.OccurredAt).HasColumnName("occurred_at");
            entity.Property(a => a.ActorUpn).HasColumnName("actor").HasMaxLength(320).IsRequired();
            entity.Property(a => a.ActorDisplayName).HasColumnName("actor_display_name").HasMaxLength(256).IsRequired();
            entity.Property(a => a.Action).HasColumnName("action").HasMaxLength(128).IsRequired();
            entity.Property(a => a.Target).HasColumnName("target").HasMaxLength(512).IsRequired();
            entity.Property(a => a.DetailsJson).HasColumnName("details").HasColumnType("jsonb").IsRequired();
            entity.Property(a => a.Outcome).HasColumnName("outcome").HasMaxLength(64).IsRequired();
            entity.Property(a => a.RunId).HasColumnName("run_id");
            entity.Property(a => a.Summary).HasColumnName("summary").HasMaxLength(1000);
            entity.Property(a => a.PrevHash).HasColumnName("prev_hash").HasMaxLength(128).IsRequired();
            entity.Property(a => a.Hash).HasColumnName("hash").HasMaxLength(128).IsRequired();
            entity.HasIndex(a => a.OccurredAt);
            entity.HasIndex(a => a.ActorUpn);
            entity.HasIndex(a => a.Target);
            entity.HasIndex(a => a.RunId);
        });

        modelBuilder.Entity<AdAgentEntity>(entity =>
        {
            entity.ToTable("ad_agent");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.Id).HasColumnName("agent_id");
            entity.Property(a => a.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
            entity.Property(a => a.EnrollmentTokenHash).HasColumnName("enrollment_token_hash").HasMaxLength(128);
            entity.Property(a => a.EnrollmentTokenExpiresAt).HasColumnName("enrollment_token_expires_at");
            entity.Property(a => a.EnrollmentTokenUsed).HasColumnName("enrollment_token_used");
            entity.Property(a => a.PublicKeyPem).HasColumnName("public_key");
            entity.Property(a => a.Hostname).HasColumnName("hostname").HasMaxLength(256);
            entity.Property(a => a.Version).HasColumnName("version").HasMaxLength(64);
            entity.Property(a => a.ProtocolVersion).HasColumnName("protocol_version");
            entity.Property(a => a.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(a => a.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(a => a.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<AppSettingEntity>(entity =>
        {
            entity.ToTable("setting");
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasColumnName("key").HasMaxLength(200);
            entity.Property(s => s.Value).HasColumnName("value").IsRequired();
            entity.Property(s => s.UpdatedAt).HasColumnName("updated_at");
        });

        modelBuilder.Entity<DirectoryCacheEntity>(entity =>
        {
            entity.ToTable("directory_cache");
            entity.HasKey(c => c.CacheKey);
            entity.Property(c => c.CacheKey).HasColumnName("object_id").HasMaxLength(256);
            entity.Property(c => c.Kind).HasColumnName("kind").HasMaxLength(64).IsRequired();
            entity.Property(c => c.DataJson).HasColumnName("data").HasColumnType("jsonb").IsRequired();
            entity.Property(c => c.FetchedAt).HasColumnName("fetched_at");
            entity.HasIndex(c => c.Kind);
        });
    }
}
