// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Rollcall.Contracts;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;

namespace Rollcall.Infrastructure.Workflow.Steps;

// --- Distribution list steps ---------------------------------------------------------------

public sealed record DistributionListMemberStepInput(string GroupIdentity, string GroupDisplayName, string MemberUpn);
public sealed record DistributionListMemberStepResult(string GroupIdentity, string MemberUpn, bool Changed);

public sealed class AddDistributionListMemberStep(IExchangeRunner exchange) : WorkflowStepBase<DistributionListMemberStepInput, DistributionListMemberStepResult>
{
    public override string Kind => "exchange.dl.addMember";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Office 365 Exchange Online", "Exchange.ManageAsApp", "Exchange Administrator", FeatureFlagKeys.Mailbox, "Add and remove members of Exchange Online distribution lists"),
    ];

    public override async Task<StepPlan> PlanAsync(DistributionListMemberStepInput input, PlanContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        if (members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase))
        {
            return new StepPlan(
                TargetSystem.ExchangeOnline,
                "Distribution lists",
                StepOperation.NoOp,
                $"Add {input.MemberUpn} to distribution list {input.GroupDisplayName}",
                input.GroupDisplayName,
                IsNoOp: true,
                NoOpReason: "Already a member");
        }

        return new StepPlan(
            TargetSystem.ExchangeOnline,
            "Distribution lists",
            StepOperation.Add,
            $"Add {input.MemberUpn} to distribution list {input.GroupDisplayName}",
            input.GroupDisplayName);
    }

    public override async Task<DistributionListMemberStepResult> ApplyAsync(DistributionListMemberStepInput input, StepContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        if (members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase))
        {
            return new DistributionListMemberStepResult(input.GroupIdentity, input.MemberUpn, false);
        }

        await exchange.AddDistributionGroupMemberAsync(input.GroupIdentity, input.MemberUpn, ctx.CancellationToken);
        return new DistributionListMemberStepResult(input.GroupIdentity, input.MemberUpn, true);
    }

    public override async Task<bool> VerifyAsync(DistributionListMemberStepInput input, DistributionListMemberStepResult result, StepContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        return members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase);
    }

    public override UndoDescriptor? DescribeUndo(DistributionListMemberStepInput input, DistributionListMemberStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "exchange.dl.removeMember",
                JsonSerializer.Serialize(input, JsonOptions),
                $"Remove {input.MemberUpn} from distribution list {input.GroupDisplayName}")
            : null;
}

public sealed class RemoveDistributionListMemberStep(IExchangeRunner exchange) : WorkflowStepBase<DistributionListMemberStepInput, DistributionListMemberStepResult>
{
    public override string Kind => "exchange.dl.removeMember";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Office 365 Exchange Online", "Exchange.ManageAsApp", "Exchange Administrator", FeatureFlagKeys.Mailbox, "Add and remove members of Exchange Online distribution lists"),
    ];

    public override async Task<StepPlan> PlanAsync(DistributionListMemberStepInput input, PlanContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        if (!members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase))
        {
            return new StepPlan(
                TargetSystem.ExchangeOnline,
                "Distribution lists",
                StepOperation.NoOp,
                $"Remove {input.MemberUpn} from distribution list {input.GroupDisplayName}",
                input.GroupDisplayName,
                IsNoOp: true,
                NoOpReason: "Not a member");
        }

        return new StepPlan(
            TargetSystem.ExchangeOnline,
            "Distribution lists",
            StepOperation.Remove,
            $"Remove {input.MemberUpn} from distribution list {input.GroupDisplayName}",
            input.GroupDisplayName);
    }

    public override async Task<DistributionListMemberStepResult> ApplyAsync(DistributionListMemberStepInput input, StepContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        if (!members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase))
        {
            return new DistributionListMemberStepResult(input.GroupIdentity, input.MemberUpn, false);
        }

        await exchange.RemoveDistributionGroupMemberAsync(input.GroupIdentity, input.MemberUpn, ctx.CancellationToken);
        return new DistributionListMemberStepResult(input.GroupIdentity, input.MemberUpn, true);
    }

    public override async Task<bool> VerifyAsync(DistributionListMemberStepInput input, DistributionListMemberStepResult result, StepContext ctx)
    {
        var members = await exchange.GetDistributionGroupMembersAsync(input.GroupIdentity, ctx.CancellationToken);
        return !members.Contains(input.MemberUpn, StringComparer.OrdinalIgnoreCase);
    }

    public override UndoDescriptor? DescribeUndo(DistributionListMemberStepInput input, DistributionListMemberStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "exchange.dl.addMember",
                JsonSerializer.Serialize(input, JsonOptions),
                $"Restore {input.MemberUpn} in distribution list {input.GroupDisplayName}")
            : null;
}

// --- Mailbox steps -------------------------------------------------------------------------

public sealed record ConvertMailboxToSharedStepInput(string MailboxIdentity);
public sealed record ConvertMailboxToSharedStepResult(string MailboxIdentity, string PreviousType, string CurrentType);

public sealed class ConvertMailboxToSharedStep(IExchangeRunner exchange) : WorkflowStepBase<ConvertMailboxToSharedStepInput, ConvertMailboxToSharedStepResult>
{
    public override string Kind => "exchange.mailbox.convertToShared";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Office 365 Exchange Online", "Exchange.ManageAsApp", "Exchange Administrator", FeatureFlagKeys.Mailbox, "Convert mailboxes to shared, configure forwarding, auto-replies, permissions, and address list visibility"),
    ];

    public override async Task<StepPlan> PlanAsync(ConvertMailboxToSharedStepInput input, PlanContext ctx)
    {
        var mailbox = await exchange.GetMailboxAsync(input.MailboxIdentity, ctx.CancellationToken);
        if (mailbox is not null && string.Equals(mailbox.RecipientTypeDetails, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
        {
            return new StepPlan(
                TargetSystem.ExchangeOnline,
                "Mailbox",
                StepOperation.NoOp,
                $"Convert mailbox {input.MailboxIdentity} to shared",
                input.MailboxIdentity,
                OldValue: "SharedMailbox",
                NewValue: "SharedMailbox",
                IsNoOp: true,
                NoOpReason: "Mailbox is already shared");
        }

        return new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Change,
            $"Convert mailbox {input.MailboxIdentity} to a shared mailbox",
            input.MailboxIdentity,
            OldValue: mailbox?.RecipientTypeDetails ?? "UserMailbox",
            NewValue: "SharedMailbox");
    }

    public override async Task<ConvertMailboxToSharedStepResult> ApplyAsync(ConvertMailboxToSharedStepInput input, StepContext ctx)
    {
        var before = await exchange.GetMailboxAsync(input.MailboxIdentity, ctx.CancellationToken);
        if (before is not null && string.Equals(before.RecipientTypeDetails, "SharedMailbox", StringComparison.OrdinalIgnoreCase))
        {
            return new ConvertMailboxToSharedStepResult(input.MailboxIdentity, "SharedMailbox", "SharedMailbox");
        }

        await exchange.ConvertMailboxToSharedAsync(input.MailboxIdentity, ctx.CancellationToken);
        return new ConvertMailboxToSharedStepResult(input.MailboxIdentity, before?.RecipientTypeDetails ?? "UserMailbox", "SharedMailbox");
    }

    public override async Task<bool> VerifyAsync(ConvertMailboxToSharedStepInput input, ConvertMailboxToSharedStepResult result, StepContext ctx)
    {
        var mailbox = await exchange.GetMailboxAsync(input.MailboxIdentity, ctx.CancellationToken);
        return mailbox is not null && string.Equals(mailbox.RecipientTypeDetails, "SharedMailbox", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record MailboxForwardingStepInput(string MailboxIdentity, string ForwardToUpn, bool DeliverToMailboxAndForward);
public sealed record MailboxForwardingStepResult(string MailboxIdentity, string ForwardToUpn);

public sealed class MailboxForwardingStep(IExchangeRunner exchange) : WorkflowStepBase<MailboxForwardingStepInput, MailboxForwardingStepResult>
{
    public override string Kind => "exchange.mailbox.configureForwarding";
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override Task<StepPlan> PlanAsync(MailboxForwardingStepInput input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Change,
            input.DeliverToMailboxAndForward
                ? $"Forward mail from {input.MailboxIdentity} to {input.ForwardToUpn} and keep a copy"
                : $"Forward mail from {input.MailboxIdentity} to {input.ForwardToUpn}",
            input.MailboxIdentity,
            NewValue: input.ForwardToUpn));

    public override async Task<MailboxForwardingStepResult> ApplyAsync(MailboxForwardingStepInput input, StepContext ctx)
    {
        await exchange.SetMailboxForwardingAsync(input.MailboxIdentity, input.ForwardToUpn, input.DeliverToMailboxAndForward, ctx.CancellationToken);
        return new MailboxForwardingStepResult(input.MailboxIdentity, input.ForwardToUpn);
    }

    public override Task<bool> VerifyAsync(MailboxForwardingStepInput input, MailboxForwardingStepResult result, StepContext ctx) =>
        Task.FromResult(true);
}

public sealed record MailboxAutoReplyStepInput(string MailboxIdentity, string? InternalMessage, string? ExternalMessage);
public sealed record MailboxAutoReplyStepResult(string MailboxIdentity, bool Enabled);

public sealed class MailboxAutoReplyStep(IExchangeRunner exchange) : WorkflowStepBase<MailboxAutoReplyStepInput, MailboxAutoReplyStepResult>
{
    public override string Kind => "exchange.mailbox.setAutoReply";
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override Task<StepPlan> PlanAsync(MailboxAutoReplyStepInput input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Change,
            $"Enable automatic replies on mailbox {input.MailboxIdentity}",
            input.MailboxIdentity));

    public override async Task<MailboxAutoReplyStepResult> ApplyAsync(MailboxAutoReplyStepInput input, StepContext ctx)
    {
        await exchange.SetMailboxAutoReplyAsync(input.MailboxIdentity, input.InternalMessage, input.ExternalMessage, ctx.CancellationToken);
        return new MailboxAutoReplyStepResult(input.MailboxIdentity, true);
    }

    public override Task<bool> VerifyAsync(MailboxAutoReplyStepInput input, MailboxAutoReplyStepResult result, StepContext ctx) =>
        Task.FromResult(result.Enabled);
}

public sealed record MailboxGrantAccessStepInput(string MailboxIdentity, string TrusteeUpn, bool FullAccess, bool SendAs);
public sealed record MailboxGrantAccessStepResult(string MailboxIdentity, string TrusteeUpn);

public sealed class MailboxGrantAccessStep(IExchangeRunner exchange) : WorkflowStepBase<MailboxGrantAccessStepInput, MailboxGrantAccessStepResult>
{
    public override string Kind => "exchange.mailbox.grantAccess";
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override Task<StepPlan> PlanAsync(MailboxGrantAccessStepInput input, PlanContext ctx)
    {
        var rights = input.FullAccess && input.SendAs
            ? "Full Access and Send As"
            : input.FullAccess
                ? "Full Access"
                : "Send As";
        return Task.FromResult(new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Add,
            $"Grant {input.TrusteeUpn} {rights} on mailbox {input.MailboxIdentity}",
            input.MailboxIdentity));
    }

    public override async Task<MailboxGrantAccessStepResult> ApplyAsync(MailboxGrantAccessStepInput input, StepContext ctx)
    {
        await exchange.GrantMailboxAccessAsync(input.MailboxIdentity, input.TrusteeUpn, input.FullAccess, input.SendAs, ctx.CancellationToken);
        return new MailboxGrantAccessStepResult(input.MailboxIdentity, input.TrusteeUpn);
    }

    public override Task<bool> VerifyAsync(MailboxGrantAccessStepInput input, MailboxGrantAccessStepResult result, StepContext ctx) =>
        Task.FromResult(true);
}

public sealed record MailboxHideFromGalStepInput(string MailboxIdentity, bool Hidden);
public sealed record MailboxHideFromGalStepResult(string MailboxIdentity, bool Hidden);

public sealed class MailboxHideFromGalStep(IExchangeRunner exchange) : WorkflowStepBase<MailboxHideFromGalStepInput, MailboxHideFromGalStepResult>
{
    public override string Kind => "exchange.mailbox.setHiddenFromGal";
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override async Task<StepPlan> PlanAsync(MailboxHideFromGalStepInput input, PlanContext ctx)
    {
        var mailbox = await exchange.GetMailboxAsync(input.MailboxIdentity, ctx.CancellationToken);
        if (mailbox is not null && mailbox.HiddenFromAddressListsEnabled == input.Hidden)
        {
            return new StepPlan(
                TargetSystem.ExchangeOnline,
                "Mailbox",
                StepOperation.NoOp,
                $"Hide {input.MailboxIdentity} from address lists",
                input.MailboxIdentity,
                IsNoOp: true,
                NoOpReason: "Already hidden from address lists");
        }

        return new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Change,
            input.Hidden
                ? $"Hide {input.MailboxIdentity} from address lists"
                : $"Show {input.MailboxIdentity} in address lists",
            input.MailboxIdentity);
    }

    public override async Task<MailboxHideFromGalStepResult> ApplyAsync(MailboxHideFromGalStepInput input, StepContext ctx)
    {
        await exchange.SetHiddenFromAddressListsAsync(input.MailboxIdentity, input.Hidden, ctx.CancellationToken);
        return new MailboxHideFromGalStepResult(input.MailboxIdentity, input.Hidden);
    }

    public override async Task<bool> VerifyAsync(MailboxHideFromGalStepInput input, MailboxHideFromGalStepResult result, StepContext ctx)
    {
        var mb = await exchange.GetMailboxAsync(input.MailboxIdentity, ctx.CancellationToken);
        return mb is null || mb.HiddenFromAddressListsEnabled == input.Hidden;
    }
}

public sealed record MailboxRegionalSettingsStepInput(string MailboxIdentity, string? TimeZone, string? Language);
public sealed record MailboxRegionalSettingsStepResult(string MailboxIdentity, bool Applied);

public sealed class MailboxRegionalSettingsStep(IExchangeRunner exchange) : WorkflowStepBase<MailboxRegionalSettingsStepInput, MailboxRegionalSettingsStepResult>
{
    public override string Kind => "exchange.mailbox.setRegionalSettings";
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override Task<StepPlan> PlanAsync(MailboxRegionalSettingsStepInput input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ExchangeOnline,
            "Mailbox",
            StepOperation.Change,
            $"Set mailbox regional settings ({input.TimeZone ?? "default"}, {input.Language ?? "default"})",
            input.MailboxIdentity));

    public override async Task<MailboxRegionalSettingsStepResult> ApplyAsync(MailboxRegionalSettingsStepInput input, StepContext ctx)
    {
        await exchange.SetMailboxRegionalSettingsAsync(input.MailboxIdentity, input.TimeZone, input.Language, ctx.CancellationToken);
        return new MailboxRegionalSettingsStepResult(input.MailboxIdentity, true);
    }

    public override Task<bool> VerifyAsync(MailboxRegionalSettingsStepInput input, MailboxRegionalSettingsStepResult result, StepContext ctx) =>
        Task.FromResult(result.Applied);
}
