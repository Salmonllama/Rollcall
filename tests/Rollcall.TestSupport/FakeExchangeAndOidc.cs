// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Rollcall.Infrastructure.Integrations;

namespace Rollcall.TestSupport;

public sealed class FakeMailbox
{
    public string Identity { get; set; } = string.Empty;
    public string RecipientTypeDetails { get; set; } = "UserMailbox";
    public double SizeGb { get; set; } = 4.2;
    public bool LitigationHoldEnabled { get; set; }
    public bool HiddenFromAddressListsEnabled { get; set; }
    public string? ForwardingSmtpAddress { get; set; }
    public bool DeliverToMailboxAndForward { get; set; }
    public string? AutoReplyState { get; set; }
    public string? InternalAutoReply { get; set; }
    public string? ExternalAutoReply { get; set; }
    public HashSet<string> FullAccessTrustees { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> SendAsTrustees { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Fake Exchange session factory exercising PooledExchangeRunner (session reuse, dead-session retry,
/// -Confirm:$false assertions, mailbox conversion, and litigation hold failure simulation).
/// </summary>
public sealed class FakeExchangeSessionFactory(TimeProvider? clock = null) : IExchangeSessionFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public int SessionsCreated { get; private set; }
    public int FailNextCommandsWithDeadSession { get; set; }
    public bool FailMailboxConversion { get; set; }
    public string MailboxConversionFailureMessage { get; set; } = "The mailbox is on litigation hold.";
    public ConcurrentQueue<ExchangeCommandRecord> CommandLog { get; } = new();
    public Dictionary<string, FakeMailbox> Mailboxes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, HashSet<string>> DistributionLists { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeMailbox EnsureMailbox(string identity, double sizeGb = 4.2, bool litigationHold = false)
    {
        if (!Mailboxes.TryGetValue(identity, out var mb))
        {
            mb = new FakeMailbox { Identity = identity, SizeGb = sizeGb, LitigationHoldEnabled = litigationHold };
            Mailboxes[identity] = mb;
        }
        else
        {
            mb.SizeGb = sizeGb;
            mb.LitigationHoldEnabled = litigationHold;
        }

        return mb;
    }

    public Task<IExchangeSession> CreateSessionAsync(CancellationToken cancellationToken)
    {
        SessionsCreated++;
        return Task.FromResult<IExchangeSession>(new FakeExchangeSession(this, _clock.GetUtcNow()));
    }

    private sealed class FakeExchangeSession(FakeExchangeSessionFactory owner, DateTimeOffset createdAt) : IExchangeSession
    {
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public bool IsAlive { get; private set; } = true;

        public Task<string> InvokeAsync(
            string cmdlet,
            IReadOnlyDictionary<string, object?> parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            owner.CommandLog.Enqueue(new ExchangeCommandRecord(
                cmdlet,
                parameters.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString()),
                owner._clock.GetUtcNow()));

            if (owner.FailNextCommandsWithDeadSession > 0)
            {
                owner.FailNextCommandsWithDeadSession--;
                IsAlive = false;
                throw new ExchangeSessionDeadException("Simulated broken Exchange session.");
            }

            switch (cmdlet)
            {
                case "Get-OrganizationConfig":
                    return Task.FromResult("{\"name\":\"Contoso\"}");

                case "Get-Mailbox":
                    {
                        var id = parameters["Identity"]!.ToString()!;
                        var mb = owner.EnsureMailbox(id);
                        return Task.FromResult(JsonSerializer.Serialize(new MailboxInfo(
                            mb.Identity,
                            mb.RecipientTypeDetails,
                            mb.SizeGb,
                            mb.LitigationHoldEnabled,
                            mb.HiddenFromAddressListsEnabled,
                            mb.ForwardingSmtpAddress,
                            mb.DeliverToMailboxAndForward,
                            mb.AutoReplyState), JsonOptions));
                    }

                case "Set-Mailbox":
                    {
                        var id = parameters["Identity"]!.ToString()!;
                        var mb = owner.EnsureMailbox(id);
                        if (parameters.TryGetValue("Type", out var type) && string.Equals(type?.ToString(), "Shared", StringComparison.OrdinalIgnoreCase))
                        {
                            if (owner.FailMailboxConversion)
                            {
                                throw new InvalidOperationException(owner.MailboxConversionFailureMessage);
                            }

                            mb.RecipientTypeDetails = "SharedMailbox";
                        }

                        if (parameters.TryGetValue("ForwardingSmtpAddress", out var fwd))
                        {
                            mb.ForwardingSmtpAddress = fwd?.ToString();
                        }

                        if (parameters.TryGetValue("DeliverToMailboxAndForward", out var keep) && keep is bool kb)
                        {
                            mb.DeliverToMailboxAndForward = kb;
                        }

                        if (parameters.TryGetValue("HiddenFromAddressListsEnabled", out var hid) && hid is bool hb)
                        {
                            mb.HiddenFromAddressListsEnabled = hb;
                        }

                        return Task.FromResult("{}");
                    }

                case "Set-MailboxAutoReplyConfiguration":
                    {
                        var id = parameters["Identity"]!.ToString()!;
                        var mb = owner.EnsureMailbox(id);
                        mb.AutoReplyState = "Enabled";
                        mb.InternalAutoReply = parameters.TryGetValue("InternalMessage", out var im) ? im?.ToString() : null;
                        mb.ExternalAutoReply = parameters.TryGetValue("ExternalMessage", out var em) ? em?.ToString() : null;
                        return Task.FromResult("{}");
                    }

                case "Add-MailboxPermission":
                    {
                        var mb = owner.EnsureMailbox(parameters["Identity"]!.ToString()!);
                        mb.FullAccessTrustees.Add(parameters["User"]!.ToString()!);
                        return Task.FromResult("{}");
                    }

                case "Add-RecipientPermission":
                    {
                        var mb = owner.EnsureMailbox(parameters["Identity"]!.ToString()!);
                        mb.SendAsTrustees.Add(parameters["Trustee"]!.ToString()!);
                        return Task.FromResult("{}");
                    }

                case "Set-MailboxRegionalConfiguration":
                    return Task.FromResult("{}");

                case "Get-DistributionGroupMember":
                    {
                        var dl = parameters["Identity"]!.ToString()!;
                        owner.DistributionLists.TryGetValue(dl, out var members);
                        return Task.FromResult(JsonSerializer.Serialize((members ?? []).ToList(), JsonOptions));
                    }

                case "Add-DistributionGroupMember":
                    {
                        var dl = parameters["Identity"]!.ToString()!;
                        var member = parameters["Member"]!.ToString()!;
                        if (!owner.DistributionLists.TryGetValue(dl, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            owner.DistributionLists[dl] = set;
                        }

                        set.Add(member);
                        return Task.FromResult("{}");
                    }

                case "Remove-DistributionGroupMember":
                    {
                        var dl = parameters["Identity"]!.ToString()!;
                        var member = parameters["Member"]!.ToString()!;
                        if (owner.DistributionLists.TryGetValue(dl, out var set))
                        {
                            set.Remove(member);
                        }

                        return Task.FromResult("{}");
                    }

                default:
                    return Task.FromResult("{}");
            }
        }

        public ValueTask DisposeAsync()
        {
            IsAlive = false;
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Mints OIDC ID tokens for integration and E2E tests (section 6.3).</summary>
public sealed class FakeOidcTokenIssuer : IDisposable
{
    private readonly RSA _rsa = RSA.Create(2048);

    public string IssueIdToken(string tenantId, string oid, string upn, string displayName)
    {
        var creds = new SigningCredentials(new RsaSecurityKey(_rsa), SecurityAlgorithms.RsaSha256);
        var jwt = new JwtSecurityToken(
            issuer: $"https://login.microsoftonline.com/{tenantId}/v2.0",
            claims:
            [
                new Claim("tid", tenantId),
                new Claim("oid", oid),
                new Claim("sub", oid),
                new Claim("preferred_username", upn),
                new Claim("name", displayName),
            ],
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public void Dispose() => _rsa.Dispose();
}
