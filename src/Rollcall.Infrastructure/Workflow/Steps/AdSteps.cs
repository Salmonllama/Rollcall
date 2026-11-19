// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Rollcall.Contracts;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;

namespace Rollcall.Infrastructure.Workflow.Steps;

public sealed record AdGenericStepResult(string JobKind, string RawResultJson);

/// <summary>Base class for workflow steps executed through the on-prem AD agent queue (M9).</summary>
public abstract class AdAgentStepBase<TInput>(IAgentDispatcher dispatcher) : WorkflowStepBase<TInput, AdGenericStepResult>
{
    public override IReadOnlyList<RequiredPermission> Permissions => [];

    protected virtual string AgentJobKind => Kind;

    public override async Task<AdGenericStepResult> ApplyAsync(TInput input, StepContext ctx)
    {
        var payload = AgentJobDispatcher.SerializePayload(input);
        var resultJson = await dispatcher.DispatchAndAwaitAsync(
            AgentJobKind,
            payload,
            ctx.RunId,
            ctx.StepId,
            TimeSpan.FromMinutes(2),
            ctx.CancellationToken);
        return new AdGenericStepResult(AgentJobKind, resultJson ?? "{}");
    }

    public override Task<bool> VerifyAsync(TInput input, AdGenericStepResult result, StepContext ctx) =>
        Task.FromResult(true);
}

public sealed class AdUserCreateStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserCreatePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserCreate;

    public override Task<StepPlan> PlanAsync(AdUserCreatePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Account",
            StepOperation.Add,
            $"Create on-prem AD user {input.DisplayName} ({input.SamAccountName}) in {input.TargetOu}",
            input.UserPrincipalName));
}

public sealed class AdUserUpdateStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserUpdatePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserUpdate;

    public override Task<StepPlan> PlanAsync(AdUserUpdatePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Attributes",
            StepOperation.Change,
            $"Update AD attributes on {input.SamAccountNameOrDn}",
            input.SamAccountNameOrDn,
            NewValue: input.Department ?? input.Title ?? input.Office ?? input.Description));
}

public sealed class AdUserSetEnabledStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserSetEnabledPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserSetEnabled;

    public override Task<StepPlan> PlanAsync(AdUserSetEnabledPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Account",
            input.Enabled ? StepOperation.Change : StepOperation.Remove,
            input.Enabled
                ? $"Enable AD account {input.SamAccountNameOrDn}"
                : $"Disable AD account {input.SamAccountNameOrDn}",
            input.SamAccountNameOrDn,
            OldValue: input.Enabled ? "Disabled" : "Enabled",
            NewValue: input.Enabled ? "Enabled" : "Disabled"));

    public override UndoDescriptor? DescribeUndo(AdUserSetEnabledPayload input, AdGenericStepResult result) =>
        new(
            Kind,
            JsonSerializer.Serialize(new AdUserSetEnabledPayload(input.SamAccountNameOrDn, !input.Enabled), JsonOptions),
            input.Enabled ? "Disable AD account again" : "Re-enable AD account");
}

public sealed class AdUserResetPasswordStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserResetPasswordPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserResetPassword;

    public override Task<StepPlan> PlanAsync(AdUserResetPasswordPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Account",
            StepOperation.Change,
            $"Reset on-prem AD password for {input.SamAccountNameOrDn}",
            input.SamAccountNameOrDn));

    protected override string SanitizeResultForPersistence(AdGenericStepResult result) =>
        JsonSerializer.Serialize(new { result.JobKind, reset = true }, JsonOptions);
}

public sealed class AdUserMoveStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserMovePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserMove;

    public override Task<StepPlan> PlanAsync(AdUserMovePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Account",
            StepOperation.Change,
            $"Move AD account {input.SamAccountNameOrDn} to {input.DestinationOu}",
            input.SamAccountNameOrDn,
            NewValue: input.DestinationOu));
}

public sealed class AdUserRemoveFromAllGroupsStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdUserRemoveFromAllGroupsPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.UserRemoveFromAllGroups;

    public override Task<StepPlan> PlanAsync(AdUserRemoveFromAllGroupsPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Remove,
            $"Remove {input.SamAccountNameOrDn} from all on-prem AD groups",
            input.SamAccountNameOrDn));

    public override UndoDescriptor? DescribeUndo(AdUserRemoveFromAllGroupsPayload input, AdGenericStepResult result) =>
        new(
            AdJobKinds.GroupAddMember,
            result.RawResultJson,
            $"Restore removed AD group memberships for {input.SamAccountNameOrDn}");
}

public sealed class AdGroupCreateStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdGroupCreatePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.GroupCreate;

    public override Task<StepPlan> PlanAsync(AdGroupCreatePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Add,
            $"Create AD {input.Scope} {input.Category} group {input.Name} in {input.TargetOu}",
            input.Name));
}

public sealed class AdGroupUpdateStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdGroupUpdatePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.GroupUpdate;

    public override Task<StepPlan> PlanAsync(AdGroupUpdatePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Change,
            $"Update AD group {input.GroupDnOrSam}",
            input.GroupDnOrSam));
}

public sealed class AdGroupMoveStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdGroupMovePayload>(dispatcher)
{
    public override string Kind => AdJobKinds.GroupMove;

    public override Task<StepPlan> PlanAsync(AdGroupMovePayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Change,
            $"Move AD group {input.GroupDnOrSam} to {input.DestinationOu}",
            input.GroupDnOrSam,
            NewValue: input.DestinationOu));
}

public sealed class AdGroupAddMemberStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdGroupMembershipPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.GroupAddMember;

    public override Task<StepPlan> PlanAsync(AdGroupMembershipPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Add,
            $"Add {input.MemberDnOrSam} to AD group {input.GroupDnOrSam}",
            input.GroupDnOrSam));
}

public sealed class AdGroupRemoveMemberStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdGroupMembershipPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.GroupRemoveMember;

    public override Task<StepPlan> PlanAsync(AdGroupMembershipPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Groups",
            StepOperation.Remove,
            $"Remove {input.MemberDnOrSam} from AD group {input.GroupDnOrSam}",
            input.GroupDnOrSam));
}

public sealed class AdSyncDeltaStep(IAgentDispatcher dispatcher) : AdAgentStepBase<AdSyncDeltaPayload>(dispatcher)
{
    public override string Kind => AdJobKinds.SyncDelta;

    public override Task<StepPlan> PlanAsync(AdSyncDeltaPayload input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.ActiveDirectory,
            "Directory sync",
            StepOperation.Info,
            string.IsNullOrWhiteSpace(input.EntraConnectServer)
                ? "Trigger Entra Connect delta sync on the AD agent host"
                : $"Trigger Entra Connect delta sync on {input.EntraConnectServer}",
            input.EntraConnectServer ?? "Entra Connect"));
}
