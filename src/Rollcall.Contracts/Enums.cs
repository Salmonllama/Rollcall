// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json.Serialization;

namespace Rollcall.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PortalRole
{
    Technician,
    Approver,
    Admin,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunType
{
    Onboarding,
    Offboarding,
    AttributeUpdate,
    BulkAttributeUpdate,
    GroupMembership,
    LicenseAssignment,
    MfaAction,
    PasswordReset,
    SessionRevoke,
    ConditionalAccessEdit,
    RestoreUser,
    AdGroupManagement,
    AdUserManagement,
    DirectorySync,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunStatus
{
    Draft,
    Planned,
    AwaitingApproval,
    Scheduled,
    Queued,
    Running,
    WaitingForAgent,
    NeedsReview,
    Succeeded,
    PartiallyFailed,
    Rejected,
    Cancelled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowStepStatus
{
    Pending,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Skipped,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StepOperation
{
    Add,
    Remove,
    Change,
    NoOp,
    Info,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetSystem
{
    EntraId,
    ExchangeOnline,
    ActiveDirectory,
    OneDrive,
    Portal,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AppCertificateStatus
{
    Pending,
    Active,
    Retiring,
    Retired,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApprovalDecisionType
{
    Approved,
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentStatus
{
    Pending,
    Connected,
    Offline,
    Revoked,
}

public static class FeatureFlagKeys
{
    public const string Licenses = "licenses";
    public const string ConditionalAccess = "conditionalAccess";
    public const string Mfa = "mfa";
    public const string Mailbox = "mailbox";
    public const string OneDrive = "oneDrive";
    public const string HybridAd = "hybridAd";

    public static readonly IReadOnlyList<string> All =
    [
        Licenses,
        ConditionalAccess,
        Mfa,
        Mailbox,
        OneDrive,
        HybridAd,
    ];
}

public static class AdJobKinds
{
    public const string UserCreate = "ad.user.create";
    public const string UserUpdate = "ad.user.update";
    public const string UserSetEnabled = "ad.user.setEnabled";
    public const string UserResetPassword = "ad.user.resetPassword";
    public const string UserMove = "ad.user.move";
    public const string UserRemoveFromAllGroups = "ad.user.removeFromAllGroups";
    public const string GroupCreate = "ad.group.create";
    public const string GroupUpdate = "ad.group.update";
    public const string GroupMove = "ad.group.move";
    public const string GroupAddMember = "ad.group.addMember";
    public const string GroupRemoveMember = "ad.group.removeMember";
    public const string OuList = "ad.ou.list";
    public const string UserGet = "ad.user.get";
    public const string GroupGet = "ad.group.get";
    public const string Search = "ad.search";
    public const string SyncDelta = "sync.delta";

    public static readonly IReadOnlyList<string> All =
    [
        UserCreate,
        UserUpdate,
        UserSetEnabled,
        UserResetPassword,
        UserMove,
        UserRemoveFromAllGroups,
        GroupCreate,
        GroupUpdate,
        GroupMove,
        GroupAddMember,
        GroupRemoveMember,
        OuList,
        UserGet,
        GroupGet,
        Search,
        SyncDelta,
    ];
}
