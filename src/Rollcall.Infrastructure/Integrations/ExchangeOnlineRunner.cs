// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Rollcall.Contracts;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;

namespace Rollcall.Infrastructure.Integrations;

/// <summary>
/// Production-ready Exchange Online runner per ADR 0002:
/// - Keeps one warm session recycled every 30 minutes or on error.
/// - Retries once on a fresh session if the current session dies mid-command (M5 criterion 7).
/// - Enforces per-cmdlet timeouts and maps Exchange errors to plain language.
/// - Verifies pinned ExchangeOnlineManagement 3.10.1 SHA-256 module tree hash before running.
/// </summary>
public sealed partial class PooledExchangeRunner(
    IExchangeSessionFactory sessionFactory,
    TimeProvider clock,
    ILogger<PooledExchangeRunner> logger) : IExchangeRunner, IAsyncDisposable
{
    public const string PinnedModuleVersion = "3.10.1";
    public const string PinnedModuleTreeSha256 = "18B5D4B687DD0CC8BDA65310B344194F097BE0DD7441934D0C127344DFF77A34";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private IExchangeSession? _currentSession;

    public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await ExecuteWithRetryAsync(
                "Get-OrganizationConfig",
                new Dictionary<string, object?>(),
                "check Exchange Online connectivity",
                cancellationToken);
            return true;
        }
        catch (StepExecutionException)
        {
            return false;
        }
    }

    public async Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default)
    {
        var json = await ExecuteWithRetryAsync(
            "Get-Mailbox",
            new Dictionary<string, object?> { ["Identity"] = identity },
            $"read mailbox {identity}",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "null")
        {
            return null;
        }

        return JsonSerializer.Deserialize<MailboxInfo>(json, JsonOptions);
    }

    public async Task ConvertMailboxToSharedAsync(string identity, CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Set-Mailbox",
            new Dictionary<string, object?> { ["Identity"] = identity, ["Type"] = "Shared" },
            $"convert mailbox {identity} to shared",
            cancellationToken);
    }

    public async Task SetMailboxForwardingAsync(
        string identity,
        string? forwardingSmtpAddress,
        bool deliverToMailboxAndForward,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Set-Mailbox",
            new Dictionary<string, object?>
            {
                ["Identity"] = identity,
                ["ForwardingSmtpAddress"] = forwardingSmtpAddress,
                ["DeliverToMailboxAndForward"] = deliverToMailboxAndForward,
            },
            $"configure forwarding on mailbox {identity}",
            cancellationToken);
    }

    public async Task SetMailboxAutoReplyAsync(
        string identity,
        string? internalMessage,
        string? externalMessage,
        CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Set-MailboxAutoReplyConfiguration",
            new Dictionary<string, object?>
            {
                ["Identity"] = identity,
                ["AutoReplyState"] = "Enabled",
                ["InternalMessage"] = internalMessage ?? string.Empty,
                ["ExternalMessage"] = externalMessage ?? internalMessage ?? string.Empty,
            },
            $"set automatic replies on mailbox {identity}",
            cancellationToken);
    }

    public async Task GrantMailboxAccessAsync(
        string identity,
        string trusteeUpn,
        bool fullAccess,
        bool sendAs,
        CancellationToken cancellationToken = default)
    {
        if (fullAccess)
        {
            await ExecuteWithRetryAsync(
                "Add-MailboxPermission",
                new Dictionary<string, object?>
                {
                    ["Identity"] = identity,
                    ["User"] = trusteeUpn,
                    ["AccessRights"] = "FullAccess",
                    ["InheritanceType"] = "All",
                    ["AutoMapping"] = true,
                },
                $"grant Full Access on mailbox {identity} to {trusteeUpn}",
                cancellationToken);
        }

        if (sendAs)
        {
            await ExecuteWithRetryAsync(
                "Add-RecipientPermission",
                new Dictionary<string, object?>
                {
                    ["Identity"] = identity,
                    ["Trustee"] = trusteeUpn,
                    ["AccessRights"] = "SendAs",
                    ["Confirm"] = false,
                },
                $"grant Send As on mailbox {identity} to {trusteeUpn}",
                cancellationToken);
        }
    }

    public async Task SetHiddenFromAddressListsAsync(string identity, bool hidden, CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Set-Mailbox",
            new Dictionary<string, object?>
            {
                ["Identity"] = identity,
                ["HiddenFromAddressListsEnabled"] = hidden,
            },
            $"update address list visibility for mailbox {identity}",
            cancellationToken);
    }

    public async Task SetMailboxRegionalSettingsAsync(
        string identity,
        string? timeZone,
        string? language,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?> { ["Identity"] = identity };
        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            parameters["TimeZone"] = timeZone;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            parameters["Language"] = language;
        }

        await ExecuteWithRetryAsync(
            "Set-MailboxRegionalConfiguration",
            parameters,
            $"configure regional settings for mailbox {identity}",
            cancellationToken);
    }

    public async Task AddDistributionGroupMemberAsync(string groupIdentity, string memberUpn, CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Add-DistributionGroupMember",
            new Dictionary<string, object?>
            {
                ["Identity"] = groupIdentity,
                ["Member"] = memberUpn,
                ["Confirm"] = false,
            },
            $"add {memberUpn} to distribution list {groupIdentity}",
            cancellationToken);
    }

    public async Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberUpn, CancellationToken cancellationToken = default)
    {
        await ExecuteWithRetryAsync(
            "Remove-DistributionGroupMember",
            new Dictionary<string, object?>
            {
                ["Identity"] = groupIdentity,
                ["Member"] = memberUpn,
                ["Confirm"] = false,
            },
            $"remove {memberUpn} from distribution list {groupIdentity}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default)
    {
        var json = await ExecuteWithRetryAsync(
            "Get-DistributionGroupMember",
            new Dictionary<string, object?> { ["Identity"] = groupIdentity },
            $"read members of distribution list {groupIdentity}",
            cancellationToken);

        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<string>>(json, JsonOptions) ?? [];
    }

    private async Task<string> ExecuteWithRetryAsync(
        string cmdlet,
        IReadOnlyDictionary<string, object?> parameters,
        string actionDescription,
        CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var session = await EnsureSessionAsync(cancellationToken);
                try
                {
                    return await session.InvokeAsync(cmdlet, parameters, DefaultCommandTimeout, cancellationToken);
                }
                catch (ExchangeSessionDeadException ex) when (attempt == 1)
                {
                    LogSessionRecycled(logger, cmdlet, ex.Message);
                    await DisposeCurrentSessionAsync();
                }
                catch (TimeoutException ex)
                {
                    await DisposeCurrentSessionAsync();
                    throw new StepExecutionException(
                        $"Exchange Online timed out while trying to {actionDescription}. Retry this step.",
                        ex.Message,
                        isTransient: true,
                        innerException: ex);
                }
                catch (StepExecutionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new StepExecutionException(
                        PlainLanguageErrorMapper.MapExchangeError(actionDescription, ex.Message),
                        SecretRedactor.Redact(ex.Message),
                        isTransient: false,
                        innerException: ex);
                }
            }

            throw new StepExecutionException(
                $"Couldn't {actionDescription} because the Exchange Online session closed repeatedly.",
                "Exchange session failed after retry.",
                isTransient: true);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<IExchangeSession> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_currentSession is not null)
        {
            var expired = clock.GetUtcNow() - _currentSession.CreatedAt >= SessionLifetime;
            if (!expired && _currentSession.IsAlive)
            {
                return _currentSession;
            }

            await DisposeCurrentSessionAsync();
        }

        _currentSession = await sessionFactory.CreateSessionAsync(cancellationToken);
        return _currentSession;
    }

    private async Task DisposeCurrentSessionAsync()
    {
        if (_currentSession is not null)
        {
            try
            {
                await _currentSession.DisposeAsync();
            }
            catch (Exception ex)
            {
                LogDisposeFailed(logger, ex.Message);
            }
            finally
            {
                _currentSession = null;
            }
        }
    }

    public static string ComputeModuleTreeSha256(string moduleRootDirectory)
    {
        var files = Directory.GetFiles(moduleRootDirectory, "*", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var sb = new StringBuilder();
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            var hash = SHA256.HashData(stream);
            sb.Append(Convert.ToHexString(hash));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeCurrentSessionAsync();
        _sessionLock.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Exchange Online session died during {Cmdlet}; retrying once on a fresh session: {Reason}")]
    private static partial void LogSessionRecycled(ILogger logger, string cmdlet, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Error disposing Exchange Online session: {Reason}")]
    private static partial void LogDisposeFailed(ILogger logger, string reason);
}

/// <summary>
/// Live Exchange Online session factory that verifies the runtime-fetched ExchangeOnlineManagement 3.10.1
/// module tree SHA-256 (ADR 0002) and invokes PowerShell with the RSA 3072 certificate passed via stdin
/// (never written to disk).
/// </summary>
public sealed class PowerShellExchangeSessionFactory(
    RollcallDbContext db,
    AppCertificateService certService,
    IConfiguration configuration,
    TimeProvider clock) : IExchangeSessionFactory
{
    public async Task<IExchangeSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken)
            ?? throw new StepExecutionException("Rollcall isn't connected to a Microsoft 365 tenant yet.", "Tenant missing.");
        var activeCert = await db.AppCertificates.AsNoTracking()
            .Where(c => c.Status == AppCertificateStatus.Active)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new StepExecutionException("No active app certificate is configured.", "Active AppCertificate missing.");

        var moduleCacheDir = configuration["Exchange:ModuleCachePath"] ?? "/var/lib/rollcall/modules";
        var moduleDir = Path.Combine(moduleCacheDir, "ExchangeOnlineManagement", PooledExchangeRunner.PinnedModuleVersion);
        if (!Directory.Exists(moduleDir))
        {
            throw new StepExecutionException(
                $"ExchangeOnlineManagement {PooledExchangeRunner.PinnedModuleVersion} isn't downloaded yet. Check outbound access to www.powershellgallery.com and Admin › Health.",
                $"Missing module directory {moduleDir}",
                isTransient: true);
        }

        var actualHash = PooledExchangeRunner.ComputeModuleTreeSha256(moduleCacheDir);
        var skipHash = string.Equals(configuration["Exchange:SkipHashCheckForDev"], "true", StringComparison.OrdinalIgnoreCase);
        if (!skipHash && !string.Equals(actualHash, PooledExchangeRunner.PinnedModuleTreeSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StepExecutionException(
                "Exchange Online module checksum verification failed. Rollcall refused to load an unverified module.",
                $"Expected SHA-256 {PooledExchangeRunner.PinnedModuleTreeSha256}, got {actualHash}.");
        }

        using var cert = certService.LoadInMemory(activeCert);
        var pfxBytes = cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx);
        var pfxBase64 = Convert.ToBase64String(pfxBytes);
        CryptographicOperations.ZeroMemory(pfxBytes);
        SecretRedactor.RegisterSecret(pfxBase64);

        return new PowerShellExchangeSession(
            tenant.AppClientId,
            tenant.PrimaryDomain,
            moduleDir,
            pfxBase64,
            clock.GetUtcNow());
    }

    private sealed class PowerShellExchangeSession(
        string clientId,
        string organization,
        string moduleDir,
        string pfxBase64,
        DateTimeOffset createdAt) : IExchangeSession
    {
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public bool IsAlive { get; private set; } = true;

        public async Task<string> InvokeAsync(
            string cmdlet,
            IReadOnlyDictionary<string, object?> parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var script = BuildScript(cmdlet, parameters);
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoLogo");
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("-");

            using var process = Process.Start(psi)
                ?? throw new ExchangeSessionDeadException("Couldn't start pwsh process for Exchange Online.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout);

            try
            {
                var fullInput = new StringBuilder();
                fullInput.AppendLine("$ErrorActionPreference = 'Stop'");
                fullInput.AppendLine(CultureInfo.InvariantCulture, $"Import-Module '{Path.Combine(moduleDir, "ExchangeOnlineManagement.psd1")}'");
                fullInput.AppendLine(CultureInfo.InvariantCulture, $"$pfx = [Convert]::FromBase64String('{pfxBase64}')");
                fullInput.AppendLine("$cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12($pfx, $null, [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)");
                fullInput.AppendLine(CultureInfo.InvariantCulture, $"Connect-ExchangeOnline -Certificate $cert -AppId '{clientId}' -Organization '{organization}' -ShowBanner:$false | Out-Null");
                fullInput.AppendLine("try {");
                fullInput.AppendLine(script);
                fullInput.AppendLine("} finally { Disconnect-ExchangeOnline -Confirm:$false -ErrorAction SilentlyContinue | Out-Null }");

                await process.StandardInput.WriteAsync(fullInput.ToString());
                process.StandardInput.Close();

                var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
                await process.WaitForExitAsync(cts.Token);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;
                if (process.ExitCode != 0)
                {
                    if (stderr.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase) ||
                        stderr.Contains("session is closed", StringComparison.OrdinalIgnoreCase))
                    {
                        IsAlive = false;
                        throw new ExchangeSessionDeadException(stderr);
                    }

                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? $"pwsh exited with {process.ExitCode}" : stderr.Trim());
                }

                return stdout.Trim();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best-effort cleanup.
                }

                IsAlive = false;
                throw new TimeoutException($"Exchange Online cmdlet {cmdlet} exceeded {timeout.TotalSeconds:0} seconds.");
            }
        }

        private static string BuildScript(string cmdlet, IReadOnlyDictionary<string, object?> parameters)
        {
            var sb = new StringBuilder();
            sb.Append(cmdlet);
            foreach (var (key, value) in parameters)
            {
                if (value is null)
                {
                    continue;
                }

                if (value is bool b)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" -{key}:${(b ? "true" : "false")}");
                }
                else
                {
                    var escaped = value.ToString()!.Replace("'", "''", StringComparison.Ordinal);
                    sb.Append(CultureInfo.InvariantCulture, $" -{key} '{escaped}'");
                }
            }

            sb.Append(" | ConvertTo-Json -Depth 4 -Compress");
            return sb.ToString();
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}
