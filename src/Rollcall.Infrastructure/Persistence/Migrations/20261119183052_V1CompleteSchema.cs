// SPDX-License-Identifier: GPL-3.0-or-later

﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Rollcall.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class V1CompleteSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ad_agent",
                columns: table => new
                {
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enrollment_token_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    enrollment_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    enrollment_token_used = table.Column<bool>(type: "boolean", nullable: false),
                    public_key = table.Column<string>(type: "text", nullable: true),
                    hostname = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    protocol_version = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ad_agent", x => x.agent_id);
                });

            migrationBuilder.CreateTable(
                name: "app_certificate",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    thumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    algorithm = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    not_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    public_certificate_der = table.Column<byte[]>(type: "bytea", nullable: false),
                    encrypted_private_key = table.Column<string>(type: "text", nullable: false),
                    key_version = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_certificate", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval_rule",
                columns: table => new
                {
                    action_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    required = table.Column<bool>(type: "boolean", nullable: false),
                    approver_role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_rule", x => x.action_type);
                });

            migrationBuilder.CreateTable(
                name: "audit_event",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    actor_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    summary = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    prev_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_event", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "directory_cache",
                columns: table => new
                {
                    object_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_directory_cache", x => x.object_id);
                });

            migrationBuilder.CreateTable(
                name: "job_queue",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    queue = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    job_kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    step_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    locked_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_queue", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "portal_user",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    entra_object_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    upn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    roles = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    disabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_sign_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_portal_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_template",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    definition = table.Column<string>(type: "jsonb", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    etag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by_upn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_template", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "scheduled_reversal",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_ref = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    action = table.Column<string>(type: "jsonb", nullable: false),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    executed = table.Column<bool>(type: "boolean", nullable: false),
                    executed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scheduled_reversal", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "setting",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    value = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_setting", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "tenant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    app_client_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    primary_domain = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    organization_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    connected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "workflow_run",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    requested_by_upn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    requested_by_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    requested_by_object_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_user_ref = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    target_user_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    plan = table.Column<string>(type: "jsonb", nullable: false),
                    plan_hash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_payload = table.Column<string>(type: "jsonb", nullable: false),
                    role_template_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role_template_version = table.Column<int>(type: "integer", nullable: true),
                    requires_approval = table.Column<bool>(type: "boolean", nullable: false),
                    status_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    credential_viewed = table.Column<bool>(type: "boolean", nullable: false),
                    credential_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    credential_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_run", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "approval",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approver_object_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    approver_upn = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    approver_display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval", x => x.id);
                    table.ForeignKey(
                        name: "FK_approval_workflow_run_run_id",
                        column: x => x.run_id,
                        principalTable: "workflow_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workflow_step",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_system = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    area = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    object_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    input = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    raw_error = table.Column<string>(type: "text", nullable: true),
                    graph_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    graph_client_request_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    result = table.Column<string>(type: "jsonb", nullable: true),
                    undo_data = table.Column<string>(type: "jsonb", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wait_started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_step", x => x.id);
                    table.ForeignKey(
                        name: "FK_workflow_step_workflow_run_run_id",
                        column: x => x.run_id,
                        principalTable: "workflow_run",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_certificate_thumbprint",
                table: "app_certificate",
                column: "thumbprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_approval_run_id",
                table: "approval",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_actor",
                table: "audit_event",
                column: "actor");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_occurred_at",
                table: "audit_event",
                column: "occurred_at");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_run_id",
                table: "audit_event",
                column: "run_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_event_target",
                table: "audit_event",
                column: "target");

            migrationBuilder.CreateIndex(
                name: "IX_directory_cache_kind",
                table: "directory_cache",
                column: "kind");

            migrationBuilder.CreateIndex(
                name: "IX_job_queue_queue_status_run_after",
                table: "job_queue",
                columns: new[] { "queue", "status", "run_after" });

            migrationBuilder.CreateIndex(
                name: "IX_portal_user_entra_object_id",
                table: "portal_user",
                column: "entra_object_id");

            migrationBuilder.CreateIndex(
                name: "IX_portal_user_upn",
                table: "portal_user",
                column: "upn",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_template_name",
                table: "role_template",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_scheduled_reversal_executed_due_at",
                table: "scheduled_reversal",
                columns: new[] { "executed", "due_at" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_created_at",
                table: "workflow_run",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_scheduled_for",
                table: "workflow_run",
                column: "scheduled_for");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_status",
                table: "workflow_run",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_run_target_user_ref",
                table: "workflow_run",
                column: "target_user_ref");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_step_run_id_seq",
                table: "workflow_step",
                columns: new[] { "run_id", "seq" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ad_agent");

            migrationBuilder.DropTable(
                name: "app_certificate");

            migrationBuilder.DropTable(
                name: "approval");

            migrationBuilder.DropTable(
                name: "approval_rule");

            migrationBuilder.DropTable(
                name: "audit_event");

            migrationBuilder.DropTable(
                name: "directory_cache");

            migrationBuilder.DropTable(
                name: "job_queue");

            migrationBuilder.DropTable(
                name: "portal_user");

            migrationBuilder.DropTable(
                name: "role_template");

            migrationBuilder.DropTable(
                name: "scheduled_reversal");

            migrationBuilder.DropTable(
                name: "setting");

            migrationBuilder.DropTable(
                name: "tenant");

            migrationBuilder.DropTable(
                name: "workflow_step");

            migrationBuilder.DropTable(
                name: "workflow_run");
        }
    }
}
