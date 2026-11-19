// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Integrations;
using Rollcall.Infrastructure.Operations;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;
using Rollcall.Infrastructure.Workflow;
using Rollcall.Infrastructure.Workflow.Steps;

namespace Rollcall.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the database context, migrator, readiness probe, and core v1 services.</summary>
    public static IServiceCollection AddRollcallDatabase(this IServiceCollection services)
    {
        // Resolved lazily so test hosts can override configuration after Program registers services.
        services.AddDbContext<RollcallDbContext>((provider, options) =>
        {
            var connectionString = provider.GetRequiredService<IConfiguration>().GetConnectionString("Rollcall")
                ?? throw new InvalidOperationException("No database connection string is configured (ConnectionStrings__Rollcall).");
            options.UseNpgsql(new NpgsqlConnectionStringBuilder(connectionString)
            {
                // The database is on the internal Compose network with password auth, and the
                // images carry no Kerberos libraries; without this Npgsql logs a missing-library error.
                GssEncryptionMode = GssEncryptionMode.Disable,
            }.ConnectionString);
        });
        services.AddScoped<DatabaseMigrator>();
        services.AddScoped<ReadinessProbe>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Registers cryptography, Graph/Exchange/Agent integrations, workflow engine, and all v1 steps.</summary>
    public static IServiceCollection AddRollcallCoreServices(this IServiceCollection services)
    {
        services.AddHttpClient("RollcallGraph", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.AddSingleton<EnvelopeEncryptionService>();
        services.AddSingleton<AppCertificateService>();
        services.AddSingleton<AgentJwtService>();
        services.AddSingleton<EphemeralCredentialStore>();

        services.AddScoped<IGraphClient, GraphApiClient>();
        services.AddScoped<IExchangeSessionFactory, PowerShellExchangeSessionFactory>();
        services.AddScoped<IExchangeRunner, PooledExchangeRunner>();
        services.AddScoped<IAgentDispatcher, AgentJobDispatcher>();
        services.AddScoped<DirectoryCacheService>();

        services.AddScoped<SettingsService>();
        services.AddScoped<AuditService>();
        services.AddScoped<JobQueueService>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<WorkflowPlanners>();
        services.AddScoped<OperationsCliCommands>();

        // Register all v1 workflow steps
        services.AddScoped<IWorkflowStep, CreateGraphUserStep>();
        services.AddScoped<IWorkflowStep, UpdateUserAttributesStep>();
        services.AddScoped<IWorkflowStep, SetManagerStep>();
        services.AddScoped<IWorkflowStep, SetAccountEnabledStep>();
        services.AddScoped<IWorkflowStep, RevokeSessionsStep>();
        services.AddScoped<IWorkflowStep, ResetCloudPasswordStep>();
        services.AddScoped<IWorkflowStep, AddCloudGroupMemberStep>();
        services.AddScoped<IWorkflowStep, RemoveCloudGroupMemberStep>();
        services.AddScoped<IWorkflowStep, BatchGroupMembersStep>();
        services.AddScoped<IWorkflowStep, AssignLicenseStep>();
        services.AddScoped<IWorkflowStep, RemoveLicenseStep>();
        services.AddScoped<IWorkflowStep, RemoveMfaMethodStep>();
        services.AddScoped<IWorkflowStep, RemoveAllMfaStep>();
        services.AddScoped<IWorkflowStep, IssueTapStep>();
        services.AddScoped<IWorkflowStep, UpdateCaMembershipStep>();
        services.AddScoped<IWorkflowStep, GrantOneDriveAccessStep>();
        services.AddScoped<IWorkflowStep, WaitForEntraSyncStep>();

        services.AddScoped<IWorkflowStep, AddDistributionListMemberStep>();
        services.AddScoped<IWorkflowStep, RemoveDistributionListMemberStep>();
        services.AddScoped<IWorkflowStep, ConvertMailboxToSharedStep>();
        services.AddScoped<IWorkflowStep, MailboxForwardingStep>();
        services.AddScoped<IWorkflowStep, MailboxAutoReplyStep>();
        services.AddScoped<IWorkflowStep, MailboxGrantAccessStep>();
        services.AddScoped<IWorkflowStep, MailboxHideFromGalStep>();
        services.AddScoped<IWorkflowStep, MailboxRegionalSettingsStep>();

        services.AddScoped<IWorkflowStep, AdUserCreateStep>();
        services.AddScoped<IWorkflowStep, AdUserUpdateStep>();
        services.AddScoped<IWorkflowStep, AdUserSetEnabledStep>();
        services.AddScoped<IWorkflowStep, AdUserResetPasswordStep>();
        services.AddScoped<IWorkflowStep, AdUserMoveStep>();
        services.AddScoped<IWorkflowStep, AdUserRemoveFromAllGroupsStep>();
        services.AddScoped<IWorkflowStep, AdGroupCreateStep>();
        services.AddScoped<IWorkflowStep, AdGroupUpdateStep>();
        services.AddScoped<IWorkflowStep, AdGroupMoveStep>();
        services.AddScoped<IWorkflowStep, AdGroupAddMemberStep>();
        services.AddScoped<IWorkflowStep, AdGroupRemoveMemberStep>();
        services.AddScoped<IWorkflowStep, AdSyncDeltaStep>();

        return services;
    }
}
