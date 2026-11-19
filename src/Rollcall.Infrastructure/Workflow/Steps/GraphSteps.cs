// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Rollcall.Contracts;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Security;

namespace Rollcall.Infrastructure.Workflow.Steps;

// --- User steps ----------------------------------------------------------------------------

public sealed record CreateGraphUserStepInput(
    string DisplayName,
    string UserPrincipalName,
    string MailNickname,
    string FirstName,
    string LastName,
    bool AccountEnabled,
    bool ForceChangePasswordNextSignIn,
    string? Department,
    string? JobTitle,
    string? OfficeLocation,
    string? CompanyName,
    string? UsageLocation,
    bool ReturnPasswordAsEphemeral);

public sealed record CreateGraphUserStepResult(
    string UserId,
    string UserPrincipalName,
    string? EphemeralPassword);

public sealed class CreateGraphUserStep(IGraphClient graph) : WorkflowStepBase<CreateGraphUserStepInput, CreateGraphUserStepResult>
{
    public override string Kind => "graph.user.create";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User.ReadWrite.All", null, "core", "Create and update cloud user accounts and attributes"),
    ];

    public override async Task<StepPlan> PlanAsync(CreateGraphUserStepInput input, PlanContext ctx)
    {
        var existing = await graph.GetUserAsync(input.UserPrincipalName, ctx.CancellationToken);
        if (existing is not null)
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Account",
                StepOperation.NoOp,
                $"User {input.UserPrincipalName} already exists in Entra ID",
                input.UserPrincipalName,
                IsNoOp: true,
                NoOpReason: "Account already exists");
        }

        var desc = input.AccountEnabled
            ? $"Create cloud user account {input.DisplayName} ({input.UserPrincipalName})"
            : $"Create cloud user account {input.DisplayName} ({input.UserPrincipalName}) with sign-in blocked until start date";
        return new StepPlan(TargetSystem.EntraId, "Account", StepOperation.Add, desc, input.UserPrincipalName);
    }

    public override async Task<CreateGraphUserStepResult> ApplyAsync(CreateGraphUserStepInput input, StepContext ctx)
    {
        var existing = await graph.GetUserAsync(input.UserPrincipalName, ctx.CancellationToken);
        if (existing is not null)
        {
            return new CreateGraphUserStepResult(existing.Id, existing.UserPrincipalName, null);
        }

        var password = EphemeralCredentialStore.GenerateStrongPassword();
        var created = await graph.CreateUserAsync(
            new GraphCreateUserRequest(
                input.DisplayName,
                input.UserPrincipalName,
                input.MailNickname,
                input.FirstName,
                input.LastName,
                password,
                input.ForceChangePasswordNextSignIn,
                input.AccountEnabled,
                input.Department,
                input.JobTitle,
                input.OfficeLocation,
                input.CompanyName,
                input.UsageLocation),
            ctx.CancellationToken);

        return new CreateGraphUserStepResult(
            created.Id,
            created.UserPrincipalName,
            input.ReturnPasswordAsEphemeral ? password : null);
    }

    public override async Task<bool> VerifyAsync(CreateGraphUserStepInput input, CreateGraphUserStepResult result, StepContext ctx) =>
        await graph.GetUserAsync(result.UserPrincipalName, ctx.CancellationToken) is not null;

    protected override (string? Kind, string? Value, DateTimeOffset? ExpiresAt) ExtractEphemeralCredential(CreateGraphUserStepResult result) =>
        string.IsNullOrWhiteSpace(result.EphemeralPassword)
            ? (null, null, null)
            : ("One-time password", result.EphemeralPassword, null);

    protected override string SanitizeResultForPersistence(CreateGraphUserStepResult result) =>
        JsonSerializer.Serialize(new { result.UserId, result.UserPrincipalName }, JsonOptions);
}

public sealed record UpdateUserAttributesStepInput(
    string UserIdOrUpn,
    string AttributeName,
    string? NewValue);

public sealed record UpdateUserAttributesStepResult(
    string UserId,
    string AttributeName,
    string? PreviousValue,
    string? AppliedValue);

public sealed class UpdateUserAttributesStep(IGraphClient graph) : WorkflowStepBase<UpdateUserAttributesStepInput, UpdateUserAttributesStepResult>
{
    public override string Kind => "graph.user.updateAttributes";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User.ReadWrite.All", null, "core", "Create and update cloud user accounts and attributes"),
    ];

    public override async Task<StepPlan> PlanAsync(UpdateUserAttributesStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        if (user is null)
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Attributes",
                StepOperation.Change,
                $"Set {FormatAttrLabel(input.AttributeName)} to {input.NewValue ?? "(empty)"}",
                input.UserIdOrUpn,
                OldValue: "(new account)",
                NewValue: input.NewValue);
        }

        if (user.OnPremisesSyncEnabled && HybridRoutingPolicy.IsAdMasteredAttribute(input.AttributeName))
        {
            var err = HybridRoutingPolicy.ValidateGraphAttributeEdit(true, input.AttributeName, false)!;
            throw new StepExecutionException(err, err);
        }

        var current = ReadCurrentAttribute(user, input.AttributeName);
        if (string.Equals(current ?? string.Empty, input.NewValue ?? string.Empty, StringComparison.Ordinal))
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Attributes",
                StepOperation.NoOp,
                $"{FormatAttrLabel(input.AttributeName)} is already {current ?? "(empty)"}",
                user.UserPrincipalName,
                OldValue: current,
                NewValue: input.NewValue,
                IsNoOp: true,
                NoOpReason: "Already set to this value");
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "Attributes",
            StepOperation.Change,
            $"Set {FormatAttrLabel(input.AttributeName)} on {user.DisplayName}",
            user.UserPrincipalName,
            OldValue: current ?? "(empty)",
            NewValue: input.NewValue ?? "(empty)");
    }

    public override async Task<UpdateUserAttributesStepResult> ApplyAsync(UpdateUserAttributesStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException(
                $"Couldn't find user {input.UserIdOrUpn} in Entra ID.",
                $"User {input.UserIdOrUpn} not found.");

        if (user.OnPremisesSyncEnabled && HybridRoutingPolicy.IsAdMasteredAttribute(input.AttributeName))
        {
            var err = HybridRoutingPolicy.ValidateGraphAttributeEdit(true, input.AttributeName, false)!;
            throw new StepExecutionException(err, err);
        }

        var current = ReadCurrentAttribute(user, input.AttributeName);
        if (string.Equals(current ?? string.Empty, input.NewValue ?? string.Empty, StringComparison.Ordinal))
        {
            return new UpdateUserAttributesStepResult(user.Id, input.AttributeName, current, input.NewValue);
        }

        var patch = BuildPatchPayload(input.AttributeName, input.NewValue);
        await graph.UpdateUserAttributesAsync(user.Id, patch, ctx.CancellationToken);
        return new UpdateUserAttributesStepResult(user.Id, input.AttributeName, current, input.NewValue);
    }

    public override async Task<bool> VerifyAsync(UpdateUserAttributesStepInput input, UpdateUserAttributesStepResult result, StepContext ctx)
    {
        var user = await graph.GetUserAsync(result.UserId, ctx.CancellationToken);
        if (user is null)
        {
            return false;
        }

        if (input.AttributeName.StartsWith("extensionAttribute", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = ReadCurrentAttribute(user, input.AttributeName);
        return string.Equals(current ?? string.Empty, input.NewValue ?? string.Empty, StringComparison.Ordinal);
    }

    public override UndoDescriptor? DescribeUndo(UpdateUserAttributesStepInput input, UpdateUserAttributesStepResult result) =>
        new(
            Kind,
            JsonSerializer.Serialize(new UpdateUserAttributesStepInput(result.UserId, input.AttributeName, result.PreviousValue), JsonOptions),
            $"Restore {FormatAttrLabel(input.AttributeName)} to {result.PreviousValue ?? "(empty)"}");

    private static string? ReadCurrentAttribute(DirectoryUserDto user, string name) => name switch
    {
        "jobTitle" => user.JobTitle,
        "department" => user.Department,
        "officeLocation" => user.OfficeLocation,
        "businessPhone" or "businessPhones" => user.BusinessPhone,
        "mobilePhone" => user.MobilePhone,
        "employeeId" => user.EmployeeId,
        "usageLocation" => user.UsageLocation,
        "companyName" => user.CompanyName,
        "displayName" => user.DisplayName,
        _ => null,
    };

    private static Dictionary<string, object?> BuildPatchPayload(string name, string? value)
    {
        if (string.Equals(name, "businessPhone", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "businessPhones", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["businessPhones"] = string.IsNullOrWhiteSpace(value) ? Array.Empty<string>() : new[] { value },
            };
        }

        if (name.StartsWith("extensionAttribute", StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, object?>
            {
                ["onPremisesExtensionAttributes"] = new Dictionary<string, object?> { [name] = value },
            };
        }

        return new Dictionary<string, object?> { [name] = value };
    }

    private static string FormatAttrLabel(string name) => name switch
    {
        "jobTitle" => "Job title",
        "department" => "Department",
        "officeLocation" => "Office location",
        "businessPhone" => "Business phone",
        "mobilePhone" => "Mobile phone",
        "employeeId" => "Employee ID",
        "usageLocation" => "Usage location",
        "companyName" => "Company name",
        "displayName" => "Display name",
        _ => name,
    };
}

public sealed record SetManagerStepInput(string UserIdOrUpn, string? ManagerId);
public sealed record SetManagerStepResult(string UserId, string? PreviousManagerId, string? NewManagerId);

public sealed class SetManagerStep(IGraphClient graph) : WorkflowStepBase<SetManagerStepInput, SetManagerStepResult>
{
    public override string Kind => "graph.user.setManager";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User.ReadWrite.All", null, "core", "Set or clear a user's manager"),
    ];

    public override async Task<StepPlan> PlanAsync(SetManagerStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var currentMgr = user is not null ? await graph.GetManagerAsync(user.Id, ctx.CancellationToken) : null;
        var newMgr = !string.IsNullOrWhiteSpace(input.ManagerId)
            ? await graph.GetUserAsync(input.ManagerId, ctx.CancellationToken)
            : null;

        if (user is not null && string.Equals(currentMgr?.Id, newMgr?.Id ?? input.ManagerId, StringComparison.OrdinalIgnoreCase))
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Attributes",
                StepOperation.NoOp,
                "Manager is already set to this user",
                user.UserPrincipalName,
                OldValue: currentMgr?.DisplayName ?? "(none)",
                NewValue: newMgr?.DisplayName ?? "(none)",
                IsNoOp: true,
                NoOpReason: "Manager unchanged");
        }

        var op = string.IsNullOrWhiteSpace(input.ManagerId) ? StepOperation.Remove : StepOperation.Change;
        var desc = string.IsNullOrWhiteSpace(input.ManagerId)
            ? $"Clear manager for {user?.DisplayName ?? input.UserIdOrUpn}"
            : $"Set manager for {user?.DisplayName ?? input.UserIdOrUpn} to {newMgr?.DisplayName ?? input.ManagerId}";
        return new StepPlan(
            TargetSystem.EntraId,
            "Attributes",
            op,
            desc,
            user?.UserPrincipalName ?? input.UserIdOrUpn,
            OldValue: currentMgr?.DisplayName ?? "(none)",
            NewValue: newMgr?.DisplayName ?? (string.IsNullOrWhiteSpace(input.ManagerId) ? "(none)" : input.ManagerId));
    }

    public override async Task<SetManagerStepResult> ApplyAsync(SetManagerStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var prev = await graph.GetManagerAsync(user.Id, ctx.CancellationToken);
        if (!string.Equals(prev?.Id, input.ManagerId, StringComparison.OrdinalIgnoreCase))
        {
            await graph.SetManagerAsync(user.Id, input.ManagerId, ctx.CancellationToken);
        }

        return new SetManagerStepResult(user.Id, prev?.Id, input.ManagerId);
    }

    public override async Task<bool> VerifyAsync(SetManagerStepInput input, SetManagerStepResult result, StepContext ctx)
    {
        var current = await graph.GetManagerAsync(result.UserId, ctx.CancellationToken);
        return string.Equals(current?.Id, input.ManagerId, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SetAccountEnabledStepInput(string UserIdOrUpn, bool AccountEnabled);
public sealed record SetAccountEnabledStepResult(string UserId, bool PreviousState, bool CurrentState);

public sealed class SetAccountEnabledStep(IGraphClient graph) : WorkflowStepBase<SetAccountEnabledStepInput, SetAccountEnabledStepResult>
{
    public override string Kind => "graph.user.setAccountEnabled";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User.EnableDisableAccount.All", null, "core", "Block or allow sign-in for cloud users"),
    ];

    public override async Task<StepPlan> PlanAsync(SetAccountEnabledStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        if (user is not null && user.AccountEnabled == input.AccountEnabled)
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Account",
                StepOperation.NoOp,
                input.AccountEnabled ? "Sign-in is already allowed" : "Sign-in is already blocked",
                user.UserPrincipalName,
                IsNoOp: true,
                NoOpReason: "Account already in target state");
        }

        var action = input.AccountEnabled ? "Allow sign-in for" : "Block sign-in for";
        var oldState = user is null
            ? (input.AccountEnabled ? "Blocked" : "Allowed")
            : (user.AccountEnabled ? "Allowed" : "Blocked");
        return new StepPlan(
            TargetSystem.EntraId,
            "Account",
            input.AccountEnabled ? StepOperation.Change : StepOperation.Remove,
            $"{action} {user?.DisplayName ?? input.UserIdOrUpn}",
            user?.UserPrincipalName ?? input.UserIdOrUpn,
            OldValue: oldState,
            NewValue: input.AccountEnabled ? "Allowed" : "Blocked");
    }

    public override async Task<SetAccountEnabledStepResult> ApplyAsync(SetAccountEnabledStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        if (user.AccountEnabled != input.AccountEnabled)
        {
            await graph.UpdateUserAttributesAsync(
                user.Id,
                new Dictionary<string, object?> { ["accountEnabled"] = input.AccountEnabled },
                ctx.CancellationToken);
        }

        return new SetAccountEnabledStepResult(user.Id, user.AccountEnabled, input.AccountEnabled);
    }

    public override async Task<bool> VerifyAsync(SetAccountEnabledStepInput input, SetAccountEnabledStepResult result, StepContext ctx)
    {
        var user = await graph.GetUserAsync(result.UserId, ctx.CancellationToken);
        return user is not null && user.AccountEnabled == input.AccountEnabled;
    }

    public override UndoDescriptor? DescribeUndo(SetAccountEnabledStepInput input, SetAccountEnabledStepResult result) =>
        new(
            Kind,
            JsonSerializer.Serialize(new SetAccountEnabledStepInput(result.UserId, result.PreviousState), JsonOptions),
            result.PreviousState ? "Allow sign-in again" : "Block sign-in again");
}

public sealed record RevokeSessionsStepInput(string UserIdOrUpn);
public sealed record RevokeSessionsStepResult(string UserId, bool Revoked);

public sealed class RevokeSessionsStep(IGraphClient graph) : WorkflowStepBase<RevokeSessionsStepInput, RevokeSessionsStepResult>
{
    public override string Kind => "graph.user.revokeSessions";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User.RevokeSessions.All", null, "core", "Sign users out of all active Microsoft 365 sessions"),
    ];

    public override async Task<StepPlan> PlanAsync(RevokeSessionsStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        return new StepPlan(
            TargetSystem.EntraId,
            "Sessions",
            StepOperation.Remove,
            $"Revoke all active sign-in sessions for {user?.DisplayName ?? input.UserIdOrUpn}",
            user?.UserPrincipalName ?? input.UserIdOrUpn);
    }

    public override async Task<RevokeSessionsStepResult> ApplyAsync(RevokeSessionsStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        await graph.RevokeSignInSessionsAsync(user.Id, ctx.CancellationToken);
        return new RevokeSessionsStepResult(user.Id, true);
    }

    public override Task<bool> VerifyAsync(RevokeSessionsStepInput input, RevokeSessionsStepResult result, StepContext ctx) =>
        Task.FromResult(result.Revoked);
}

public sealed record ResetCloudPasswordStepInput(string UserIdOrUpn, bool ForceChangeNextSignIn, bool ShowCredentialOnce);
public sealed record ResetCloudPasswordStepResult(string UserId, string UserPrincipalName, string? EphemeralPassword);

public sealed class ResetCloudPasswordStep(IGraphClient graph) : WorkflowStepBase<ResetCloudPasswordStepInput, ResetCloudPasswordStepResult>
{
    public override string Kind => "graph.user.resetPassword";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "User-PasswordProfile.ReadWrite.All", "User Administrator", "core", "Reset passwords for cloud users"),
    ];

    public override async Task<StepPlan> PlanAsync(ResetCloudPasswordStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var desc = input.ShowCredentialOnce
            ? $"Reset password for {user?.DisplayName ?? input.UserIdOrUpn} (new password shown once)"
            : $"Reset password for {user?.DisplayName ?? input.UserIdOrUpn} to an unshown random value";
        return new StepPlan(
            TargetSystem.EntraId,
            "Account",
            StepOperation.Change,
            desc,
            user?.UserPrincipalName ?? input.UserIdOrUpn);
    }

    public override async Task<ResetCloudPasswordStepResult> ApplyAsync(ResetCloudPasswordStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var newPassword = EphemeralCredentialStore.GenerateStrongPassword();
        await graph.ResetPasswordAsync(user.Id, newPassword, input.ForceChangeNextSignIn, ctx.CancellationToken);
        return new ResetCloudPasswordStepResult(user.Id, user.UserPrincipalName, input.ShowCredentialOnce ? newPassword : null);
    }

    public override Task<bool> VerifyAsync(ResetCloudPasswordStepInput input, ResetCloudPasswordStepResult result, StepContext ctx) =>
        Task.FromResult(true);

    protected override (string? Kind, string? Value, DateTimeOffset? ExpiresAt) ExtractEphemeralCredential(ResetCloudPasswordStepResult result) =>
        string.IsNullOrWhiteSpace(result.EphemeralPassword)
            ? (null, null, null)
            : ("One-time password", result.EphemeralPassword, null);

    protected override string SanitizeResultForPersistence(ResetCloudPasswordStepResult result) =>
        JsonSerializer.Serialize(new { result.UserId, result.UserPrincipalName, reset = true }, JsonOptions);
}

// --- Group steps ---------------------------------------------------------------------------

public sealed record GroupMemberStepInput(string UserIdOrUpn, string GroupId, string? GroupDisplayName);
public sealed record GroupMemberStepResult(string UserId, string GroupId, string GroupDisplayName, bool Changed);

public sealed class AddCloudGroupMemberStep(IGraphClient graph) : WorkflowStepBase<GroupMemberStepInput, GroupMemberStepResult>
{
    public override string Kind => "graph.group.addMember";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "GroupMember.ReadWrite.All", null, "core", "Add and remove members of security and Microsoft 365 groups"),
    ];

    public override async Task<StepPlan> PlanAsync(GroupMemberStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var groupName = input.GroupDisplayName;
        if (string.IsNullOrWhiteSpace(groupName))
        {
            var detail = await graph.GetGroupDetailAsync(input.GroupId, ctx.CancellationToken);
            groupName = detail?.Group.DisplayName ?? input.GroupId;
        }

        if (user is not null)
        {
            var existingGroups = await graph.GetUserGroupsAsync(user.Id, ctx.CancellationToken);
            if (existingGroups.Any(g => string.Equals(g.Id, input.GroupId, StringComparison.OrdinalIgnoreCase)))
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "Groups",
                    StepOperation.NoOp,
                    $"Add {user.DisplayName} to group {groupName}",
                    groupName,
                    IsNoOp: true,
                    NoOpReason: "Already a member");
            }
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "Groups",
            StepOperation.Add,
            $"Add {user?.DisplayName ?? input.UserIdOrUpn} to group {groupName}",
            groupName);
    }

    public override async Task<GroupMemberStepResult> ApplyAsync(GroupMemberStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var existing = await graph.GetUserGroupsAsync(user.Id, ctx.CancellationToken);
        var groupName = input.GroupDisplayName ?? input.GroupId;
        if (existing.Any(g => string.Equals(g.Id, input.GroupId, StringComparison.OrdinalIgnoreCase)))
        {
            return new GroupMemberStepResult(user.Id, input.GroupId, groupName, false);
        }

        await graph.AddGroupMemberAsync(input.GroupId, user.Id, ctx.CancellationToken);
        return new GroupMemberStepResult(user.Id, input.GroupId, groupName, true);
    }

    public override async Task<bool> VerifyAsync(GroupMemberStepInput input, GroupMemberStepResult result, StepContext ctx)
    {
        var groups = await graph.GetUserGroupsAsync(result.UserId, ctx.CancellationToken);
        return groups.Any(g => string.Equals(g.Id, result.GroupId, StringComparison.OrdinalIgnoreCase));
    }

    public override UndoDescriptor? DescribeUndo(GroupMemberStepInput input, GroupMemberStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "graph.group.removeMember",
                JsonSerializer.Serialize(new GroupMemberStepInput(result.UserId, result.GroupId, result.GroupDisplayName), JsonOptions),
                $"Remove from group {result.GroupDisplayName}")
            : null;
}

public sealed class RemoveCloudGroupMemberStep(IGraphClient graph) : WorkflowStepBase<GroupMemberStepInput, GroupMemberStepResult>
{
    public override string Kind => "graph.group.removeMember";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "GroupMember.ReadWrite.All", null, "core", "Add and remove members of security and Microsoft 365 groups"),
    ];

    public override async Task<StepPlan> PlanAsync(GroupMemberStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var groupName = input.GroupDisplayName ?? input.GroupId;
        if (user is not null)
        {
            var existingGroups = await graph.GetUserGroupsAsync(user.Id, ctx.CancellationToken);
            if (!existingGroups.Any(g => string.Equals(g.Id, input.GroupId, StringComparison.OrdinalIgnoreCase)))
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "Groups",
                    StepOperation.NoOp,
                    $"Remove {user.DisplayName} from group {groupName}",
                    groupName,
                    IsNoOp: true,
                    NoOpReason: "Not a member");
            }
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "Groups",
            StepOperation.Remove,
            $"Remove {user?.DisplayName ?? input.UserIdOrUpn} from group {groupName}",
            groupName);
    }

    public override async Task<GroupMemberStepResult> ApplyAsync(GroupMemberStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var existing = await graph.GetUserGroupsAsync(user.Id, ctx.CancellationToken);
        var groupName = input.GroupDisplayName ?? input.GroupId;
        if (!existing.Any(g => string.Equals(g.Id, input.GroupId, StringComparison.OrdinalIgnoreCase)))
        {
            return new GroupMemberStepResult(user.Id, input.GroupId, groupName, false);
        }

        await graph.RemoveGroupMemberAsync(input.GroupId, user.Id, ctx.CancellationToken);
        return new GroupMemberStepResult(user.Id, input.GroupId, groupName, true);
    }

    public override async Task<bool> VerifyAsync(GroupMemberStepInput input, GroupMemberStepResult result, StepContext ctx)
    {
        var groups = await graph.GetUserGroupsAsync(result.UserId, ctx.CancellationToken);
        return !groups.Any(g => string.Equals(g.Id, result.GroupId, StringComparison.OrdinalIgnoreCase));
    }

    public override UndoDescriptor? DescribeUndo(GroupMemberStepInput input, GroupMemberStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "graph.group.addMember",
                JsonSerializer.Serialize(new GroupMemberStepInput(result.UserId, result.GroupId, result.GroupDisplayName), JsonOptions),
                $"Restore membership in group {result.GroupDisplayName}")
            : null;
}

public sealed record BatchGroupMembersStepInput(string GroupId, string GroupDisplayName, IReadOnlyList<string> UserIds, bool Remove);
public sealed record BatchGroupMembersStepResult(string GroupId, int SucceededCount, int FailedCount);

public sealed class BatchGroupMembersStep(IGraphClient graph) : WorkflowStepBase<BatchGroupMembersStepInput, BatchGroupMembersStepResult>
{
    public override string Kind => "graph.group.batchMembers";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "GroupMember.ReadWrite.All", null, "core", "Bulk add and remove members of security and Microsoft 365 groups"),
    ];

    public override Task<StepPlan> PlanAsync(BatchGroupMembersStepInput input, PlanContext ctx)
    {
        var verb = input.Remove ? "Remove" : "Add";
        var prep = input.Remove ? "from" : "to";
        var op = input.Remove ? StepOperation.Remove : StepOperation.Add;
        var warnings = input.UserIds.Count >= 25
            ? new[] { $"Bulk operation affecting {input.UserIds.Count} users using Graph $batch." }
            : null;
        return Task.FromResult(new StepPlan(
            TargetSystem.EntraId,
            "Groups",
            op,
            $"{verb} {input.UserIds.Count} users {prep} group {input.GroupDisplayName}",
            input.GroupDisplayName,
            Warnings: warnings));
    }

    public override async Task<BatchGroupMembersStepResult> ApplyAsync(BatchGroupMembersStepInput input, StepContext ctx)
    {
        var ops = new List<GraphBatchOperation>(input.UserIds.Count);
        for (var i = 0; i < input.UserIds.Count; i++)
        {
            var uid = input.UserIds[i];
            if (input.Remove)
            {
                ops.Add(new GraphBatchOperation(
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    "DELETE",
                    $"/groups/{input.GroupId}/members/{uid}/$ref"));
            }
            else
            {
                ops.Add(new GraphBatchOperation(
                    (i + 1).ToString(CultureInfo.InvariantCulture),
                    "POST",
                    $"/groups/{input.GroupId}/members/$ref",
                    new Dictionary<string, string> { ["@odata.id"] = $"https://graph.microsoft.com/v1.0/directoryObjects/{uid}" }));
            }
        }

        var results = await graph.ExecuteBatchAsync(ops, ctx.CancellationToken);
        var failed = results.Where(r => r.Status >= 400 && r.Status != 404).ToList();
        if (failed.Count > 0)
        {
            throw new StepExecutionException(
                $"Couldn't update {failed.Count} of {input.UserIds.Count} memberships in {input.GroupDisplayName}.",
                string.Join("; ", failed.Select(f => $"#{f.Id}: HTTP {f.Status} {f.ErrorMessage}")));
        }

        return new BatchGroupMembersStepResult(input.GroupId, results.Count - failed.Count, failed.Count);
    }

    public override Task<bool> VerifyAsync(BatchGroupMembersStepInput input, BatchGroupMembersStepResult result, StepContext ctx) =>
        Task.FromResult(result.FailedCount == 0);
}

// --- License steps -------------------------------------------------------------------------

public sealed record AssignLicenseStepInput(string UserIdOrUpn, string SkuId, IReadOnlyList<string> DisabledPlans);
public sealed record AssignLicenseStepResult(string UserId, string SkuId, string FriendlyName, bool Changed);

public sealed class AssignLicenseStep(IGraphClient graph) : WorkflowStepBase<AssignLicenseStepInput, AssignLicenseStepResult>
{
    public override string Kind => "graph.license.assign";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "LicenseAssignment.ReadWrite.All", null, FeatureFlagKeys.Licenses, "Assign and remove Microsoft 365 licenses"),
    ];

    public override async Task<StepPlan> PlanAsync(AssignLicenseStepInput input, PlanContext ctx)
    {
        var skus = await graph.ListSubscribedSkusAsync(ctx.CancellationToken);
        var sku = skus.FirstOrDefault(s => string.Equals(s.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase))
            ?? throw new StepExecutionException($"License SKU {input.SkuId} doesn't exist in this tenant.", $"Unknown SKU {input.SkuId}.");

        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        if (user is not null)
        {
            var current = await graph.GetUserLicensesAsync(user.Id, ctx.CancellationToken);
            if (current.Any(l => string.Equals(l.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase)))
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "Licenses",
                    StepOperation.NoOp,
                    $"Assign {sku.FriendlyName} to {user.DisplayName}",
                    sku.FriendlyName,
                    IsNoOp: true,
                    NoOpReason: "License already assigned");
            }
        }

        if (sku.AvailableSeats <= 0)
        {
            throw new StepExecutionException(
                $"No {sku.FriendlyName} licenses available (0 of {sku.TotalSeats}).",
                $"SKU {sku.SkuId} ({sku.SkuPartNumber}) has 0 available seats out of {sku.TotalSeats}.");
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "Licenses",
            StepOperation.Add,
            $"Assign license {sku.FriendlyName} ({sku.AvailableSeats} of {sku.TotalSeats} seats available)",
            sku.FriendlyName);
    }

    public override async Task<AssignLicenseStepResult> ApplyAsync(AssignLicenseStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var skus = await graph.ListSubscribedSkusAsync(ctx.CancellationToken);
        var sku = skus.FirstOrDefault(s => string.Equals(s.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase));
        var friendly = sku?.FriendlyName ?? input.SkuId;

        var current = await graph.GetUserLicensesAsync(user.Id, ctx.CancellationToken);
        if (current.Any(l => string.Equals(l.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase)))
        {
            return new AssignLicenseStepResult(user.Id, input.SkuId, friendly, false);
        }

        if (sku is not null && sku.AvailableSeats <= 0)
        {
            throw new StepExecutionException(
                $"No {sku.FriendlyName} licenses available (0 of {sku.TotalSeats}).",
                $"SKU {sku.SkuId} has 0 available seats.");
        }

        await graph.AssignLicensesAsync(
            user.Id,
            [new TemplateLicenseSpec(input.SkuId, input.DisabledPlans)],
            [],
            ctx.CancellationToken);

        return new AssignLicenseStepResult(user.Id, input.SkuId, friendly, true);
    }

    public override async Task<bool> VerifyAsync(AssignLicenseStepInput input, AssignLicenseStepResult result, StepContext ctx)
    {
        var licenses = await graph.GetUserLicensesAsync(result.UserId, ctx.CancellationToken);
        return licenses.Any(l => string.Equals(l.SkuId, result.SkuId, StringComparison.OrdinalIgnoreCase));
    }

    public override UndoDescriptor? DescribeUndo(AssignLicenseStepInput input, AssignLicenseStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "graph.license.remove",
                JsonSerializer.Serialize(new RemoveLicenseStepInput(result.UserId, result.SkuId, result.FriendlyName), JsonOptions),
                $"Remove license {result.FriendlyName}")
            : null;
}

public sealed record RemoveLicenseStepInput(string UserIdOrUpn, string SkuId, string? FriendlyName);
public sealed record RemoveLicenseStepResult(string UserId, string SkuId, string FriendlyName, IReadOnlyList<string> DisabledPlans, bool Changed);

public sealed class RemoveLicenseStep(IGraphClient graph) : WorkflowStepBase<RemoveLicenseStepInput, RemoveLicenseStepResult>
{
    public override string Kind => "graph.license.remove";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "LicenseAssignment.ReadWrite.All", null, FeatureFlagKeys.Licenses, "Assign and remove Microsoft 365 licenses"),
    ];

    public override async Task<StepPlan> PlanAsync(RemoveLicenseStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var label = input.FriendlyName ?? input.SkuId;
        if (user is not null)
        {
            var current = await graph.GetUserLicensesAsync(user.Id, ctx.CancellationToken);
            var existing = current.FirstOrDefault(l => string.Equals(l.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "Licenses",
                    StepOperation.NoOp,
                    $"Remove license {label}",
                    label,
                    IsNoOp: true,
                    NoOpReason: "License not assigned");
            }

            if (existing.AssignedViaGroup)
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "Licenses",
                    StepOperation.NoOp,
                    $"License {existing.FriendlyName} is assigned through group {existing.SourceGroupName}",
                    existing.FriendlyName,
                    IsNoOp: true,
                    NoOpReason: $"Assigned via group {existing.SourceGroupName}");
            }

            label = existing.FriendlyName;
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "Licenses",
            StepOperation.Remove,
            $"Remove license {label} from {user?.DisplayName ?? input.UserIdOrUpn}",
            label);
    }

    public override async Task<RemoveLicenseStepResult> ApplyAsync(RemoveLicenseStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var current = await graph.GetUserLicensesAsync(user.Id, ctx.CancellationToken);
        var existing = current.FirstOrDefault(l => string.Equals(l.SkuId, input.SkuId, StringComparison.OrdinalIgnoreCase));
        if (existing is null || existing.AssignedViaGroup)
        {
            return new RemoveLicenseStepResult(user.Id, input.SkuId, existing?.FriendlyName ?? input.FriendlyName ?? input.SkuId, [], false);
        }

        await graph.AssignLicensesAsync(user.Id, [], [input.SkuId], ctx.CancellationToken);
        return new RemoveLicenseStepResult(user.Id, input.SkuId, existing.FriendlyName, existing.DisabledPlans, true);
    }

    public override async Task<bool> VerifyAsync(RemoveLicenseStepInput input, RemoveLicenseStepResult result, StepContext ctx)
    {
        var licenses = await graph.GetUserLicensesAsync(result.UserId, ctx.CancellationToken);
        return !licenses.Any(l => string.Equals(l.SkuId, result.SkuId, StringComparison.OrdinalIgnoreCase) && !l.AssignedViaGroup);
    }

    public override UndoDescriptor? DescribeUndo(RemoveLicenseStepInput input, RemoveLicenseStepResult result) =>
        result.Changed
            ? new UndoDescriptor(
                "graph.license.assign",
                JsonSerializer.Serialize(new AssignLicenseStepInput(result.UserId, result.SkuId, result.DisabledPlans), JsonOptions),
                $"Restore license {result.FriendlyName}")
            : null;
}

// --- MFA & TAP steps -----------------------------------------------------------------------

public sealed record RemoveMfaMethodStepInput(string UserIdOrUpn, string MethodId, string MethodType);
public sealed record RemoveMfaMethodStepResult(string UserId, string MethodId, bool Removed);

public sealed class RemoveMfaMethodStep(IGraphClient graph) : WorkflowStepBase<RemoveMfaMethodStepInput, RemoveMfaMethodStepResult>
{
    public override string Kind => "graph.mfa.removeMethod";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "UserAuthenticationMethod.ReadWrite.All", "Authentication Administrator", FeatureFlagKeys.Mfa, "Manage MFA methods and issue Temporary Access Passes"),
    ];

    public override async Task<StepPlan> PlanAsync(RemoveMfaMethodStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        if (user is not null)
        {
            var methods = await graph.GetUserMfaMethodsAsync(user.Id, ctx.CancellationToken);
            if (!methods.Any(m => string.Equals(m.Id, input.MethodId, StringComparison.OrdinalIgnoreCase)))
            {
                return new StepPlan(
                    TargetSystem.EntraId,
                    "MFA",
                    StepOperation.NoOp,
                    $"Remove MFA method {input.MethodType}",
                    input.MethodType,
                    IsNoOp: true,
                    NoOpReason: "Method already removed");
            }
        }

        return new StepPlan(
            TargetSystem.EntraId,
            "MFA",
            StepOperation.Remove,
            $"Remove MFA method {input.MethodType} from {user?.DisplayName ?? input.UserIdOrUpn}",
            input.MethodType);
    }

    public override async Task<RemoveMfaMethodStepResult> ApplyAsync(RemoveMfaMethodStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var methods = await graph.GetUserMfaMethodsAsync(user.Id, ctx.CancellationToken);
        if (!methods.Any(m => string.Equals(m.Id, input.MethodId, StringComparison.OrdinalIgnoreCase)))
        {
            return new RemoveMfaMethodStepResult(user.Id, input.MethodId, false);
        }

        await graph.RemoveMfaMethodAsync(user.Id, input.MethodType, input.MethodId, ctx.CancellationToken);
        return new RemoveMfaMethodStepResult(user.Id, input.MethodId, true);
    }

    public override async Task<bool> VerifyAsync(RemoveMfaMethodStepInput input, RemoveMfaMethodStepResult result, StepContext ctx)
    {
        var methods = await graph.GetUserMfaMethodsAsync(result.UserId, ctx.CancellationToken);
        return !methods.Any(m => string.Equals(m.Id, result.MethodId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record RemoveAllMfaStepInput(string UserIdOrUpn, bool RevokeSessions);
public sealed record RemoveAllMfaStepResult(string UserId, int RemovedCount);

public sealed class RemoveAllMfaStep(IGraphClient graph) : WorkflowStepBase<RemoveAllMfaStepInput, RemoveAllMfaStepResult>
{
    public override string Kind => "graph.mfa.requireReRegistration";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "UserAuthenticationMethod.ReadWrite.All", "Authentication Administrator", FeatureFlagKeys.Mfa, "Remove all registered MFA methods and require re-registration"),
    ];

    public override async Task<StepPlan> PlanAsync(RemoveAllMfaStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var removableCount = 0;
        if (user is not null)
        {
            var methods = await graph.GetUserMfaMethodsAsync(user.Id, ctx.CancellationToken);
            removableCount = methods.Count(m => m.Removable);
        }

        if (user is not null && removableCount == 0 && !input.RevokeSessions)
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "MFA",
                StepOperation.NoOp,
                $"Remove registered MFA methods for {user.DisplayName}",
                user.UserPrincipalName,
                IsNoOp: true,
                NoOpReason: "No removable MFA methods registered");
        }

        var desc = input.RevokeSessions
            ? $"Remove {removableCount} MFA method(s) and revoke sessions for {user?.DisplayName ?? input.UserIdOrUpn}"
            : $"Remove {removableCount} registered MFA method(s) for {user?.DisplayName ?? input.UserIdOrUpn}";
        return new StepPlan(
            TargetSystem.EntraId,
            "MFA",
            StepOperation.Remove,
            desc,
            user?.UserPrincipalName ?? input.UserIdOrUpn);
    }

    public override async Task<RemoveAllMfaStepResult> ApplyAsync(RemoveAllMfaStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var methods = (await graph.GetUserMfaMethodsAsync(user.Id, ctx.CancellationToken))
            .Where(m => m.Removable)
            .ToList();
        foreach (var m in methods)
        {
            await graph.RemoveMfaMethodAsync(user.Id, m.MethodType, m.Id, ctx.CancellationToken);
        }

        if (input.RevokeSessions)
        {
            await graph.RevokeSignInSessionsAsync(user.Id, ctx.CancellationToken);
        }

        return new RemoveAllMfaStepResult(user.Id, methods.Count);
    }

    public override async Task<bool> VerifyAsync(RemoveAllMfaStepInput input, RemoveAllMfaStepResult result, StepContext ctx)
    {
        var remaining = await graph.GetUserMfaMethodsAsync(result.UserId, ctx.CancellationToken);
        return remaining.All(m => !m.Removable);
    }
}

public sealed record IssueTapStepInput(string UserIdOrUpn, int LifetimeInMinutes, bool IsUsableOnce);
public sealed record IssueTapStepResult(string UserId, string UserPrincipalName, string TapId, DateTimeOffset ExpiresAt, string? EphemeralTap);

public sealed class IssueTapStep(IGraphClient graph) : WorkflowStepBase<IssueTapStepInput, IssueTapStepResult>
{
    public override string Kind => "graph.mfa.issueTap";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "UserAuthenticationMethod.ReadWrite.All", "Authentication Administrator", FeatureFlagKeys.Mfa, "Issue a Temporary Access Pass for first sign-in or recovery"),
    ];

    public override async Task<StepPlan> PlanAsync(IssueTapStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var mode = input.IsUsableOnce ? "one-time" : "multi-use";
        return new StepPlan(
            TargetSystem.EntraId,
            "MFA",
            StepOperation.Add,
            $"Issue {mode} Temporary Access Pass valid for {input.LifetimeInMinutes} minutes (shown once)",
            user?.UserPrincipalName ?? input.UserIdOrUpn);
    }

    public override async Task<IssueTapStepResult> ApplyAsync(IssueTapStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var tap = await graph.IssueTemporaryAccessPassAsync(user.Id, input.LifetimeInMinutes, input.IsUsableOnce, ctx.CancellationToken);
        SecretRedactor.RegisterSecret(tap.TemporaryAccessPass);
        return new IssueTapStepResult(
            user.Id,
            user.UserPrincipalName,
            tap.Id,
            tap.StartDateTime.AddMinutes(tap.LifetimeInMinutes),
            tap.TemporaryAccessPass);
    }

    public override Task<bool> VerifyAsync(IssueTapStepInput input, IssueTapStepResult result, StepContext ctx) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(result.TapId));

    protected override (string? Kind, string? Value, DateTimeOffset? ExpiresAt) ExtractEphemeralCredential(IssueTapStepResult result) =>
        ("Temporary Access Pass", result.EphemeralTap, result.ExpiresAt);

    // M5 criterion 5: TAP is NEVER persisted in DB, logs, or audit details.
    protected override string SanitizeResultForPersistence(IssueTapStepResult result) =>
        JsonSerializer.Serialize(new { result.UserId, result.UserPrincipalName, result.TapId, result.ExpiresAt }, JsonOptions);
}

// --- Conditional Access step ---------------------------------------------------------------

public sealed record UpdateCaMembershipStepInput(
    string PolicyId,
    IReadOnlyList<string> IncludeUsers,
    IReadOnlyList<string> ExcludeUsers,
    IReadOnlyList<string> IncludeGroups,
    IReadOnlyList<string> ExcludeGroups,
    string? ExpectedETag,
    DateTimeOffset? TemporaryExclusionExpiresAt,
    string? TemporaryExcludedUserId);

public sealed record UpdateCaMembershipStepResult(
    string PolicyId,
    string PolicyDisplayName,
    string NewETag);

public sealed class UpdateCaMembershipStep(IGraphClient graph) : WorkflowStepBase<UpdateCaMembershipStepInput, UpdateCaMembershipStepResult>
{
    public override string Kind => "graph.ca.updateMembership";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "Policy.Read.All", null, FeatureFlagKeys.ConditionalAccess, "Read Conditional Access policies"),
        new("Microsoft Graph", "Policy.ReadWrite.ConditionalAccess", null, FeatureFlagKeys.ConditionalAccess, "Edit Conditional Access policy include and exclude targets"),
    ];

    public override async Task<StepPlan> PlanAsync(UpdateCaMembershipStepInput input, PlanContext ctx)
    {
        var policy = await graph.GetConditionalAccessPolicyAsync(input.PolicyId, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find Conditional Access policy {input.PolicyId}.", "CA policy not found.");

        if (!string.IsNullOrWhiteSpace(input.ExpectedETag) &&
            !string.Equals(policy.ETag, input.ExpectedETag, StringComparison.Ordinal))
        {
            throw new StepExecutionException(
                $"Conditional Access policy \"{policy.DisplayName}\" was edited outside Rollcall since this change was planned. Review the current policy and re-plan.",
                $"ETag mismatch: expected {input.ExpectedETag}, actual {policy.ETag}.");
        }

        var safety = CaSafetyRailEvaluator.Evaluate(
            policy,
            input.IncludeUsers,
            input.ExcludeUsers,
            input.IncludeGroups,
            input.ExcludeGroups,
            ctx.BreakGlassAccountIds,
            ctx.BreakGlassGroupIds);

        if (safety.BlockingErrors.Count > 0)
        {
            throw new StepExecutionException(string.Join(" ", safety.BlockingErrors), string.Join("; ", safety.BlockingErrors));
        }

        var unchanged =
            SetEquals(policy.IncludeUsers, input.IncludeUsers) &&
            SetEquals(policy.ExcludeUsers, input.ExcludeUsers) &&
            SetEquals(policy.IncludeGroups, input.IncludeGroups) &&
            SetEquals(policy.ExcludeGroups, input.ExcludeGroups);

        if (unchanged)
        {
            return new StepPlan(
                TargetSystem.EntraId,
                "Conditional Access",
                StepOperation.NoOp,
                $"Update targets on Conditional Access policy {policy.DisplayName}",
                policy.DisplayName,
                OldValue: policy.ETag,
                NewValue: policy.ETag,
                IsNoOp: true,
                NoOpReason: "Policy targets unchanged",
                RequiresApproval: safety.RequiresApproval,
                Warnings: safety.Warnings);
        }

        var desc = input.TemporaryExclusionExpiresAt.HasValue
            ? $"Add temporary exclusion until {input.TemporaryExclusionExpiresAt.Value.ToString("MMM d, yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture)} on {policy.DisplayName}"
            : $"Update user and group targets on Conditional Access policy {policy.DisplayName}";

        return new StepPlan(
            TargetSystem.EntraId,
            "Conditional Access",
            StepOperation.Change,
            desc,
            policy.DisplayName,
            OldValue: $"state={policy.State}, etag={policy.ETag}, incU={policy.IncludeUsers.Count}, excU={policy.ExcludeUsers.Count}, incG={policy.IncludeGroups.Count}, excG={policy.ExcludeGroups.Count}",
            NewValue: $"incU={input.IncludeUsers.Count}, excU={input.ExcludeUsers.Count}, incG={input.IncludeGroups.Count}, excG={input.ExcludeGroups.Count}",
            RequiresApproval: safety.RequiresApproval,
            Warnings: safety.Warnings);
    }

    public override async Task<UpdateCaMembershipStepResult> ApplyAsync(UpdateCaMembershipStepInput input, StepContext ctx)
    {
        var policy = await graph.GetConditionalAccessPolicyAsync(input.PolicyId, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find Conditional Access policy {input.PolicyId}.", "CA policy not found.");

        await graph.PatchConditionalAccessUsersAsync(
            input.PolicyId,
            input.IncludeUsers,
            input.ExcludeUsers,
            input.IncludeGroups,
            input.ExcludeGroups,
            ctx.CancellationToken);

        var updated = await graph.GetConditionalAccessPolicyAsync(input.PolicyId, ctx.CancellationToken);
        return new UpdateCaMembershipStepResult(input.PolicyId, policy.DisplayName, updated?.ETag ?? policy.ETag);
    }

    public override async Task<bool> VerifyAsync(UpdateCaMembershipStepInput input, UpdateCaMembershipStepResult result, StepContext ctx)
    {
        var policy = await graph.GetConditionalAccessPolicyAsync(input.PolicyId, ctx.CancellationToken);
        return policy is not null &&
            SetEquals(policy.IncludeUsers, input.IncludeUsers) &&
            SetEquals(policy.ExcludeUsers, input.ExcludeUsers) &&
            SetEquals(policy.IncludeGroups, input.IncludeGroups) &&
            SetEquals(policy.ExcludeGroups, input.ExcludeGroups);
    }

    private static bool SetEquals(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        new HashSet<string>(a, StringComparer.OrdinalIgnoreCase).SetEquals(b);
}

// --- OneDrive steps ------------------------------------------------------------------------

public sealed record GrantOneDriveAccessStepInput(string UserIdOrUpn, string ManagerUpn, int RetentionDays);
public sealed record GrantOneDriveAccessStepResult(string UserId, string ManagerUpn, string PermissionId);

public sealed class GrantOneDriveAccessStep(IGraphClient graph) : WorkflowStepBase<GrantOneDriveAccessStepInput, GrantOneDriveAccessStepResult>
{
    public override string Kind => "graph.onedrive.grantAccess";

    public override IReadOnlyList<RequiredPermission> Permissions =>
    [
        new("Microsoft Graph", "Sites.FullControl.All", null, FeatureFlagKeys.OneDrive, "Grant and expire manager access to a departing user's OneDrive"),
    ];

    public override async Task<StepPlan> PlanAsync(GrantOneDriveAccessStepInput input, PlanContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken);
        var retention = await graph.GetOneDriveRetentionDaysAsync(ctx.CancellationToken);
        var warnings = retention.HasValue
            ? new[] { $"Tenant OneDrive retention period for deleted users is {retention.Value} days." }
            : new[] { "Couldn't read tenant OneDrive retention period; confirm SharePoint retention settings before deleting the account." };

        return new StepPlan(
            TargetSystem.OneDrive,
            "OneDrive",
            StepOperation.Add,
            $"Grant {input.ManagerUpn} access to {user?.DisplayName ?? input.UserIdOrUpn}'s OneDrive for {input.RetentionDays} days",
            user?.UserPrincipalName ?? input.UserIdOrUpn,
            Warnings: warnings);
    }

    public override async Task<GrantOneDriveAccessStepResult> ApplyAsync(GrantOneDriveAccessStepInput input, StepContext ctx)
    {
        var user = await graph.GetUserAsync(input.UserIdOrUpn, ctx.CancellationToken)
            ?? throw new StepExecutionException($"Couldn't find user {input.UserIdOrUpn} in Entra ID.", "User not found.");
        var grant = await graph.GrantOneDriveAccessAsync(user.Id, input.ManagerUpn, ctx.CancellationToken);
        return new GrantOneDriveAccessStepResult(user.Id, input.ManagerUpn, grant.PermissionId);
    }

    public override Task<bool> VerifyAsync(GrantOneDriveAccessStepInput input, GrantOneDriveAccessStepResult result, StepContext ctx) =>
        Task.FromResult(!string.IsNullOrWhiteSpace(result.PermissionId));
}

// --- Directory sync WaitFor step -----------------------------------------------------------

public sealed record WaitForEntraSyncStepInput(string SamAccountName, string ExpectedUpn, int TimeoutMinutes);
public sealed record WaitForEntraSyncStepResult(string UserId, string UserPrincipalName);

public sealed class WaitForEntraSyncStep(IGraphClient graph) : WorkflowStepBase<WaitForEntraSyncStepInput, WaitForEntraSyncStepResult>
{
    public override string Kind => "graph.user.waitForSync";

    public override IReadOnlyList<RequiredPermission> Permissions => [];

    public override Task<StepPlan> PlanAsync(WaitForEntraSyncStepInput input, PlanContext ctx) =>
        Task.FromResult(new StepPlan(
            TargetSystem.EntraId,
            "Directory sync",
            StepOperation.Info,
            $"Wait up to {input.TimeoutMinutes} minutes for {input.ExpectedUpn} to sync from Active Directory into Entra ID",
            input.ExpectedUpn));

    public override async Task<WaitForEntraSyncStepResult> ApplyAsync(WaitForEntraSyncStepInput input, StepContext ctx)
    {
        var deadline = ctx.Clock.GetUtcNow().AddMinutes(Math.Max(1, input.TimeoutMinutes));
        while (ctx.Clock.GetUtcNow() <= deadline)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            var user = await graph.GetUserBySamAccountNameAsync(input.SamAccountName, ctx.CancellationToken)
                ?? await graph.GetUserAsync(input.ExpectedUpn, ctx.CancellationToken);
            if (user is not null)
            {
                return new WaitForEntraSyncStepResult(user.Id, user.UserPrincipalName);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), ctx.CancellationToken);
        }

        throw new StepExecutionException(
            $"Timed out waiting for {input.ExpectedUpn} to appear in Entra ID after {input.TimeoutMinutes} minutes. Check Entra Connect sync health in the Troubleshooting guide, then retry this step.",
            $"User {input.SamAccountName}/{input.ExpectedUpn} did not appear in Entra ID within {input.TimeoutMinutes} minutes.",
            isTransient: false);
    }

    public override async Task<bool> VerifyAsync(WaitForEntraSyncStepInput input, WaitForEntraSyncStepResult result, StepContext ctx) =>
        await graph.GetUserAsync(result.UserId, ctx.CancellationToken) is not null;
}
