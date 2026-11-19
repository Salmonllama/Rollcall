// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Policies;
using Rollcall.Domain.Workflow;

namespace Rollcall.Domain.Tests;

public sealed class DomainPoliciesTests
{
    [Fact]
    public void Ca_applicability_handles_direct_group_all_and_exclude_override()
    {
        var policyAllWithExclude = new ConditionalAccessPolicyDto(
            "p1", "Require MFA", "enabled", "e1",
            ["All"], ["u1"], [], [], [], [], ["mfa"], true, false, "All cloud apps", []);
        var excludedAll = CaApplicabilityEvaluator.Evaluate(policyAllWithExclude, "u1", new HashSet<string>(), new HashSet<string>());
        Assert.Equal("Excluded", excludedAll.Applicability);

        var appliedAll = CaApplicabilityEvaluator.Evaluate(policyAllWithExclude, "u2", new HashSet<string>(), new HashSet<string>());
        Assert.Equal("Applies", appliedAll.Applicability);

        var policyDirectInclude = policyAllWithExclude with { IncludeUsers = ["u2"], ExcludeUsers = [] };
        Assert.Equal("Applies", CaApplicabilityEvaluator.Evaluate(policyDirectInclude, "u2", new HashSet<string>(), new HashSet<string>()).Applicability);

        var policyGroupIncludeDirectExclude = policyAllWithExclude with { IncludeUsers = [], IncludeGroups = ["g1"], ExcludeUsers = ["u2"] };
        Assert.Equal("Excluded", CaApplicabilityEvaluator.Evaluate(policyGroupIncludeDirectExclude, "u2", new HashSet<string> { "g1" }, new HashSet<string>()).Applicability);
        Assert.Equal("Applies", CaApplicabilityEvaluator.Evaluate(policyGroupIncludeDirectExclude, "u3", new HashSet<string> { "g1" }, new HashSet<string>()).Applicability);

        var policyReportOnly = policyAllWithExclude with { State = "enabledForReportingButNotEnforced", ExcludeUsers = [] };
        Assert.Equal("Report-only", CaApplicabilityEvaluator.Evaluate(policyReportOnly, "u2", new HashSet<string>(), new HashSet<string>()).Applicability);
    }

    [Fact]
    public void Ca_safety_rails_block_empty_includes_and_breakglass_removal_and_warn_on_mfa_exclusion()
    {
        var policy = new ConditionalAccessPolicyDto(
            "p1", "Require MFA for all", "enabled", "e1",
            ["All"], ["bg-1"], [], ["bg-grp"], [], [], ["mfa"], true, false, "All cloud apps", []);

        var emptyInclude = CaSafetyRailEvaluator.Evaluate(policy, [], ["bg-1"], [], ["bg-grp"], ["bg-1"], ["bg-grp"]);
        Assert.NotEmpty(emptyInclude.BlockingErrors);

        var removeBreakGlass = CaSafetyRailEvaluator.Evaluate(policy, ["All"], [], [], ["bg-grp"], ["bg-1"], ["bg-grp"]);
        Assert.Contains(removeBreakGlass.BlockingErrors, e => e.Contains("bg-1", StringComparison.Ordinal));

        var excludeFromMfa = CaSafetyRailEvaluator.Evaluate(policy, ["All"], ["bg-1", "u2"], [], ["bg-grp"], ["bg-1"], ["bg-grp"]);
        Assert.Empty(excludeFromMfa.BlockingErrors);
        Assert.Contains(excludeFromMfa.Warnings, w => w.Contains("requires MFA", StringComparison.Ordinal));
        Assert.True(excludeFromMfa.RequiresApproval);
    }

    [Fact]
    public void Offboarding_order_fails_if_license_removal_precedes_shared_mailbox_conversion()
    {
        string[] invalidOrder =
        [
            "graph.user.setAccountEnabled",
            "graph.user.revokeSessions",
            "graph.license.remove",
            "exchange.mailbox.convertToShared",
        ];

        Assert.Throws<InvalidOperationException>(() => OffboardingDomainLogic.AssertValidStepOrder(invalidOrder));

        string[] validOrder =
        [
            "graph.user.setAccountEnabled",
            "graph.user.revokeSessions",
            "exchange.mailbox.convertToShared",
            "graph.license.remove",
        ];

        OffboardingDomainLogic.AssertValidStepOrder(validOrder);
    }

    [Fact]
    public void Offboarding_preflight_warns_on_large_mailbox_and_hold_and_blocks_technician_on_privileged_role()
    {
        var (warnings, blocking) = OffboardingDomainLogic.EvaluatePreflight(
            mailboxSizeGb: 62.5,
            litigationHoldEnabled: true,
            ownedGroupNames: ["Sales Team"],
            userDirectoryRoles: ["Global Administrator"],
            actorRoles: [PortalRole.Technician],
            confirmPrivilegedRoleAsAdmin: false);

        Assert.Contains(warnings, w => w.Contains("50 GB", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("litigation hold", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("Sales Team", StringComparison.Ordinal));
        Assert.Contains(blocking, b => b.Contains("Admin role", StringComparison.Ordinal));

        var (_, adminBlocking) = OffboardingDomainLogic.EvaluatePreflight(
            5.0, false, [], ["Global Administrator"], [PortalRole.Admin], confirmPrivilegedRoleAsAdmin: true);
        Assert.Empty(adminBlocking);
    }

    [Fact]
    public void Onboarding_collision_detection_flags_upn_nickname_displayname_and_sam_without_inventing_alternatives()
    {
        var existing = new[]
        {
            new DirectoryUserDto("u1", "jane.doe@contoso.com", "Jane Doe", "jane.doe@contoso.com",
                null, null, null, null, "US", null, null, null, true, true, "jdoe", null, "Member", 1, null),
        };

        var upn = OnboardingDomainLogic.RenderPattern("{first}.{last}@{domain}", "Jane", "Doe", "contoso.com");
        var display = OnboardingDomainLogic.RenderPattern("{first} {last}", "Jane", "Doe", "contoso.com");
        Assert.Equal("jane.doe@contoso.com", upn);
        Assert.Equal("Jane Doe", display);

        var collisions = OnboardingDomainLogic.DetectCollisions(upn, "jane.doe", display, "jdoe", existing);
        Assert.Equal(4, collisions.Count);
        Assert.Contains(collisions, c => c.Field == "userPrincipalName");
        Assert.Contains(collisions, c => c.Field == "mailNickname");
        Assert.Contains(collisions, c => c.Field == "displayName");
        Assert.Contains(collisions, c => c.Field == "sAMAccountName");
    }

    [Fact]
    public void Hybrid_routing_blocks_ad_mastered_graph_edit_when_hybrid_agent_disabled()
    {
        var err = HybridRoutingPolicy.ValidateGraphAttributeEdit(onPremisesSyncEnabled: true, "department", hybridAdEnabled: false);
        Assert.NotNull(err);
        Assert.Contains("on-prem Active Directory", err, StringComparison.Ordinal);

        Assert.Null(HybridRoutingPolicy.ValidateGraphAttributeEdit(onPremisesSyncEnabled: true, "usageLocation", hybridAdEnabled: false));
        Assert.Equal(TargetSystem.ActiveDirectory, HybridRoutingPolicy.ResolveAttributeTarget(true, "department"));
        Assert.Equal(TargetSystem.EntraId, HybridRoutingPolicy.ResolveAttributeTarget(true, "usageLocation"));
    }

    [Fact]
    public void Role_policy_forbids_self_approval_even_for_admins()
    {
        Assert.False(RoleAuthorizationPolicy.CanApproveRun([PortalRole.Admin, PortalRole.Approver], "alex@contoso.com", "alex@contoso.com", PortalRole.Approver));
        Assert.True(RoleAuthorizationPolicy.CanApproveRun([PortalRole.Approver], "pat@contoso.com", "alex@contoso.com", PortalRole.Approver));
        Assert.False(RoleAuthorizationPolicy.CanApproveRun([PortalRole.Approver], "pat@contoso.com", "alex@contoso.com", PortalRole.Admin));
        Assert.True(RoleAuthorizationPolicy.CanApproveRun([PortalRole.Admin], "sam@contoso.com", "alex@contoso.com", PortalRole.Admin));
    }

    [Fact]
    public void Audit_hash_chain_detects_first_tampered_row()
    {
        var e1 = new AuditEventEntity
        {
            Id = 1,
            OccurredAt = DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ActorUpn = "a@contoso.com",
            ActorDisplayName = "A",
            Action = "run.created",
            Target = "u1",
            Outcome = "queued",
            DetailsJson = "{}",
            PrevHash = AuditHashChain.GenesisHash,
        };
        e1.Hash = AuditHashChain.ComputeHash(e1);

        var e2 = new AuditEventEntity
        {
            Id = 2,
            OccurredAt = DateTimeOffset.Parse("2026-11-01T10:01:00Z", System.Globalization.CultureInfo.InvariantCulture),
            ActorUpn = "a@contoso.com",
            ActorDisplayName = "A",
            Action = "run.finished",
            Target = "u1",
            Outcome = "succeeded",
            DetailsJson = "{}",
            PrevHash = e1.Hash,
        };
        e2.Hash = AuditHashChain.ComputeHash(e2);

        Assert.True(AuditHashChain.Verify([e1, e2]).IsValid);

        e1.Outcome = "tampered";
        var broken = AuditHashChain.Verify([e1, e2]);
        Assert.False(broken.IsValid);
        Assert.Equal(1, broken.FirstBrokenEventId);
    }
}
