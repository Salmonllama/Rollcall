// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;

namespace Rollcall.Api.Endpoints;

public static class AgentProtocolEndpoints
{
    public static void MapAgentProtocolEndpoints(this WebApplication app)
    {
        app.MapPost("/api/agent/enroll", async (
            AgentEnrollRequest request,
            RollcallDbContext db,
            SettingsService settings,
            AuditService audit,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            if (request.ProtocolVersion < AgentJwtService.MinimumProtocolVersion)
            {
                return Results.Problem(
                    title: "Agent update required",
                    detail: $"This agent uses protocol version {request.ProtocolVersion}, below the minimum {AgentJwtService.MinimumProtocolVersion}. Update the AD agent and try again.",
                    statusCode: StatusCodes.Status426UpgradeRequired);
            }

            var tokenHash = AgentJwtService.HashEnrollmentToken(request.EnrollmentToken);
            var now = clock.GetUtcNow();
            var agent = await db.AdAgents.FirstOrDefaultAsync(a => a.EnrollmentTokenHash == tokenHash, ct);
            if (agent is null || agent.EnrollmentTokenUsed || agent.Status == AgentStatus.Revoked ||
                (agent.EnrollmentTokenExpiresAt.HasValue && agent.EnrollmentTokenExpiresAt.Value < now))
            {
                return Results.Problem(
                    title: "Invalid enrollment token",
                    detail: "The enrollment token is invalid, expired, or has already been used. Generate a new enrollment command in Admin › Agents.",
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(request.PublicKeyPem) ||
                !request.PublicKeyPem.Contains("BEGIN PUBLIC KEY", StringComparison.Ordinal))
            {
                return Results.BadRequest(new { detail = "A valid PEM-encoded ECDSA P-256 public key is required." });
            }

            // Store ONLY the public key; never a private key or shared secret (M9 criterion 1).
            agent.PublicKeyPem = request.PublicKeyPem.Trim();
            agent.EnrollmentTokenUsed = true;
            agent.EnrollmentTokenHash = null;
            agent.Hostname = request.Hostname;
            agent.Version = request.Version;
            agent.ProtocolVersion = request.ProtocolVersion;
            agent.Status = AgentStatus.Connected;
            agent.LastSeenAt = now;
            await db.SaveChangesAsync(ct);

            await audit.AppendAsync(
                $"agent:{agent.Id}",
                agent.Name,
                "agent.enrolled",
                agent.Name,
                "succeeded",
                new { agent.Id, agent.Hostname, agent.Version },
                summary: $"AD agent \"{agent.Name}\" enrolled from {agent.Hostname}.",
                cancellationToken: ct);

            var adSettings = await settings.GetAdSettingsAsync(ct);
            return Results.Ok(new AgentEnrollResponse(
                agent.Id,
                agent.Name,
                adSettings.AllowedOus,
                adSettings.EntraConnectServer,
                AgentJwtService.MinimumProtocolVersion));
        });

        app.MapPost("/api/agent/heartbeat", async (
            AgentHeartbeatRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            AgentJwtService jwtService,
            SettingsService settings,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (agent, authErr) = await AuthenticateAgentAsync(ctx, db, jwtService, request.ProtocolVersion, ct);
            if (authErr is not null)
            {
                return authErr;
            }

            agent!.Hostname = request.Hostname;
            agent.Version = request.Version;
            agent.ProtocolVersion = request.ProtocolVersion;
            agent.LastSeenAt = clock.GetUtcNow();
            agent.Status = AgentStatus.Connected;
            await db.SaveChangesAsync(ct);

            var adSettings = await settings.GetAdSettingsAsync(ct);
            return Results.Ok(new AgentHeartbeatResponse(adSettings.AllowedOus, adSettings.EntraConnectServer, false));
        });

        app.MapPost("/api/agent/jobs/lease", async (
            AgentJobLeaseRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            AgentJwtService jwtService,
            JobQueueService jobQueue,
            SettingsService settings,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (agent, authErr) = await AuthenticateAgentAsync(ctx, db, jwtService, request.ProtocolVersion, ct);
            if (authErr is not null)
            {
                return authErr;
            }

            agent!.LastSeenAt = clock.GetUtcNow();
            agent.Status = AgentStatus.Connected;
            await db.SaveChangesAsync(ct);

            var adSettings = await settings.GetAdSettingsAsync(ct);
            var waitSeconds = Math.Clamp(request.WaitSeconds, 0, 30);
            var deadline = clock.GetUtcNow().AddSeconds(waitSeconds);
            IReadOnlyList<JobQueueItemEntity> leased = [];

            while (true)
            {
                leased = await jobQueue.LeaseAsync(
                    "agent",
                    agent.Id.ToString("D"),
                    Math.Clamp(request.MaxJobs, 1, 10),
                    TimeSpan.FromMinutes(5),
                    ct);
                if (leased.Count > 0 || clock.GetUtcNow() >= deadline || waitSeconds == 0)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }

            var items = leased.Select(j => new AgentJobLeaseItemDto(
                j.Id,
                j.RunId,
                j.StepId,
                j.JobKind,
                j.PayloadJson,
                j.LockedUntil ?? clock.GetUtcNow().AddMinutes(5),
                adSettings.AllowedOus)).ToList();

            return Results.Ok(new AgentJobLeaseResponse(items));
        });

        app.MapPost("/api/agent/jobs/{jobId:guid}/progress", async (
            Guid jobId,
            AgentJobProgressRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            AgentJwtService jwtService,
            TimeProvider clock,
            CancellationToken ct) =>
        {
            var (agent, authErr) = await AuthenticateAgentAsync(ctx, db, jwtService, AgentJwtService.MinimumProtocolVersion, ct);
            if (authErr is not null)
            {
                return authErr;
            }

            var job = await db.JobQueue.FirstOrDefaultAsync(j => j.Id == jobId && j.Queue == "agent", ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            job.LockedUntil = clock.GetUtcNow().AddMinutes(5);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { jobId, request.Message, request.PercentComplete });
        });

        app.MapPost("/api/agent/jobs/{jobId:guid}/result", async (
            Guid jobId,
            AgentJobResultRequest request,
            HttpContext ctx,
            RollcallDbContext db,
            AgentJwtService jwtService,
            JobQueueService jobQueue,
            CancellationToken ct) =>
        {
            var (agent, authErr) = await AuthenticateAgentAsync(ctx, db, jwtService, AgentJwtService.MinimumProtocolVersion, ct);
            if (authErr is not null)
            {
                return authErr;
            }

            var job = await db.JobQueue.FirstOrDefaultAsync(j => j.Id == jobId && j.Queue == "agent", ct);
            if (job is null)
            {
                return Results.NotFound();
            }

            if (request.Succeeded)
            {
                await jobQueue.CompleteAsync(jobId, request.ResultJson ?? "{}", ct);
            }
            else
            {
                var err = request.PlainLanguageError ?? request.RawError ?? "AD agent job failed.";
                await jobQueue.FailAsync(jobId, err, requeue: request.IsTransient, cancellationToken: ct);
            }

            return Results.Ok(new { recorded = true });
        });
    }

    private static async Task<(AdAgentEntity? Agent, IResult? Error)> AuthenticateAgentAsync(
        HttpContext ctx,
        RollcallDbContext db,
        AgentJwtService jwtService,
        int protocolVersion,
        CancellationToken ct)
    {
        if (protocolVersion < AgentJwtService.MinimumProtocolVersion)
        {
            return (null, Results.Problem(
                title: "Agent update required",
                detail: $"Agent protocol version {protocolVersion} is below the minimum supported version {AgentJwtService.MinimumProtocolVersion}.",
                statusCode: StatusCodes.Status426UpgradeRequired));
        }

        var authHeader = ctx.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Results.Unauthorized());
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var agentId = AgentJwtService.ExtractUnvalidatedAgentId(token);
        if (!agentId.HasValue)
        {
            return (null, Results.Unauthorized());
        }

        var agent = await db.AdAgents.FirstOrDefaultAsync(a => a.Id == agentId.Value, ct);
        if (agent is null || !jwtService.ValidateAgentJwt(token, agent))
        {
            return (null, Results.Unauthorized());
        }

        return (agent, null);
    }
}
