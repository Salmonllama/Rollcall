// SPDX-License-Identifier: GPL-3.0-or-later

namespace Rollcall.Infrastructure.Integrations;

public sealed record MailboxInfo(
    string Identity,
    string RecipientTypeDetails,
    double SizeGb,
    bool LitigationHoldEnabled,
    bool HiddenFromAddressListsEnabled,
    string? ForwardingSmtpAddress,
    bool DeliverToMailboxAndForward,
    string? AutoReplyState);

public sealed record ExchangeCommandRecord(
    string Cmdlet,
    IReadOnlyDictionary<string, string?> Parameters,
    DateTimeOffset ExecutedAt);

public interface IExchangeRunner
{
    Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default);
    Task<MailboxInfo?> GetMailboxAsync(string identity, CancellationToken cancellationToken = default);
    Task ConvertMailboxToSharedAsync(string identity, CancellationToken cancellationToken = default);
    Task SetMailboxForwardingAsync(string identity, string? forwardingSmtpAddress, bool deliverToMailboxAndForward, CancellationToken cancellationToken = default);
    Task SetMailboxAutoReplyAsync(string identity, string? internalMessage, string? externalMessage, CancellationToken cancellationToken = default);
    Task GrantMailboxAccessAsync(string identity, string trusteeUpn, bool fullAccess, bool sendAs, CancellationToken cancellationToken = default);
    Task SetHiddenFromAddressListsAsync(string identity, bool hidden, CancellationToken cancellationToken = default);
    Task SetMailboxRegionalSettingsAsync(string identity, string? timeZone, string? language, CancellationToken cancellationToken = default);
    Task AddDistributionGroupMemberAsync(string groupIdentity, string memberUpn, CancellationToken cancellationToken = default);
    Task RemoveDistributionGroupMemberAsync(string groupIdentity, string memberUpn, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetDistributionGroupMembersAsync(string groupIdentity, CancellationToken cancellationToken = default);
}

public interface IExchangeSession : IAsyncDisposable
{
    DateTimeOffset CreatedAt { get; }
    bool IsAlive { get; }
    Task<string> InvokeAsync(string cmdlet, IReadOnlyDictionary<string, object?> parameters, TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IExchangeSessionFactory
{
    Task<IExchangeSession> CreateSessionAsync(CancellationToken cancellationToken);
}

public sealed class ExchangeSessionDeadException(string message) : Exception(message);
