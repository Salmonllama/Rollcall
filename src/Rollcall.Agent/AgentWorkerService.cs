// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Rollcall.Contracts;

namespace Rollcall.Agent;

/// <summary>
/// Outbound-only Windows service host for the Rollcall AD agent (section 4.7).
/// Opens zero listening ports; authenticates every request with a 5-minute ECDSA P-256 JWT.
/// </summary>
public sealed partial class AgentWorkerService(
    IHttpClientFactory httpClientFactory,
    AgentJobProcessor processor,
    IConfiguration configuration,
    ILogger<AgentWorkerService> logger) : BackgroundService
{
    public const string AgentVersion = "1.0.0";
    public const int ProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var portalUrl = configuration["Agent:PortalUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(portalUrl))
        {
            LogMissingPortalUrl(logger);
            return;
        }

        var stateDir = configuration["Agent:StateDirectory"]
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RollcallAgent");
        using var keyStore = AgentKeyStore.LoadOrCreate(stateDir);

        var agentIdFile = Path.Combine(stateDir, "agent-id.txt");
        Guid agentId;
        IReadOnlyList<string> allowedOus = [];

        if (File.Exists(agentIdFile) && Guid.TryParse((await File.ReadAllTextAsync(agentIdFile, stoppingToken)).Trim(), out var existingId))
        {
            agentId = existingId;
        }
        else
        {
            var enrollmentToken = configuration["Agent:EnrollmentToken"];
            if (string.IsNullOrWhiteSpace(enrollmentToken))
            {
                LogMissingToken(logger);
                return;
            }

            var enrollResp = await EnrollAsync(portalUrl, enrollmentToken, keyStore.PublicKeyPem, stoppingToken);
            agentId = enrollResp.AgentId;
            allowedOus = enrollResp.AllowedOus;
            await File.WriteAllTextAsync(agentIdFile, agentId.ToString("D"), stoppingToken);
            LogEnrolled(logger, agentId, portalUrl);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hb = await SendHeartbeatAsync(portalUrl, agentId, keyStore, stoppingToken);
                if (hb is not null)
                {
                    if (hb.Revoked)
                    {
                        LogRevoked(logger, agentId);
                        return;
                    }

                    allowedOus = hb.AllowedOus;
                }

                var leased = await LeaseJobsAsync(portalUrl, agentId, keyStore, stoppingToken);
                foreach (var job in leased)
                {
                    var effectiveOus = job.AllowedOus.Count > 0 ? job.AllowedOus : allowedOus;
                    var result = await processor.ExecuteAsync(job.Kind, job.PayloadJson, effectiveOus, stoppingToken);
                    await PostResultAsync(portalUrl, agentId, keyStore, job.JobId, result, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                LogLoopError(logger, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<AgentEnrollResponse> EnrollAsync(
        string portalUrl,
        string token,
        string publicKeyPem,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("RollcallPortal");
        using var response = await client.PostAsJsonAsync(
            new Uri($"{portalUrl}/api/agent/enroll"),
            new AgentEnrollRequest(token, publicKeyPem, Environment.MachineName, AgentVersion, ProtocolVersion),
            JsonOptions,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentEnrollResponse>(JsonOptions, cancellationToken))!;
    }

    private async Task<AgentHeartbeatResponse?> SendHeartbeatAsync(
        string portalUrl,
        Guid agentId,
        AgentKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        using var req = CreateAuthenticatedRequest(HttpMethod.Post, $"{portalUrl}/api/agent/heartbeat", agentId, keyStore);
        req.Content = JsonContent.Create(new AgentHeartbeatRequest(Environment.MachineName, AgentVersion, ProtocolVersion), options: JsonOptions);
        var client = httpClientFactory.CreateClient("RollcallPortal");
        using var resp = await client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }

        return await resp.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(JsonOptions, cancellationToken);
    }

    private async Task<IReadOnlyList<AgentJobLeaseItemDto>> LeaseJobsAsync(
        string portalUrl,
        Guid agentId,
        AgentKeyStore keyStore,
        CancellationToken cancellationToken)
    {
        using var req = CreateAuthenticatedRequest(HttpMethod.Post, $"{portalUrl}/api/agent/jobs/lease", agentId, keyStore);
        req.Content = JsonContent.Create(new AgentJobLeaseRequest(5, 30, AgentVersion, ProtocolVersion), options: JsonOptions);
        var client = httpClientFactory.CreateClient("RollcallPortal");
        using var resp = await client.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<AgentJobLeaseResponse>(JsonOptions, cancellationToken);
        return payload?.Jobs ?? [];
    }

    private async Task PostResultAsync(
        string portalUrl,
        Guid agentId,
        AgentKeyStore keyStore,
        Guid jobId,
        AgentJobResultRequest result,
        CancellationToken cancellationToken)
    {
        using var req = CreateAuthenticatedRequest(HttpMethod.Post, $"{portalUrl}/api/agent/jobs/{jobId}/result", agentId, keyStore);
        req.Content = JsonContent.Create(result, options: JsonOptions);
        var client = httpClientFactory.CreateClient("RollcallPortal");
        using var resp = await client.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(
        HttpMethod method,
        string url,
        Guid agentId,
        AgentKeyStore keyStore)
    {
        var req = new HttpRequestMessage(method, new Uri(url));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keyStore.CreateSignedJwt(agentId));
        return req;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent:PortalUrl is not configured. Reinstall with install-agent.ps1.")]
    private static partial void LogMissingPortalUrl(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Agent is not enrolled and Agent:EnrollmentToken is not set.")]
    private static partial void LogMissingToken(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled AD agent {AgentId} with {PortalUrl}.")]
    private static partial void LogEnrolled(ILogger logger, Guid agentId, string portalUrl);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AD agent {AgentId} has been revoked by an administrator; stopping.")]
    private static partial void LogRevoked(ILogger logger, Guid agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "AD agent poll cycle failed; retrying in 5 seconds: {Reason}")]
    private static partial void LogLoopError(ILogger logger, string reason);
}
