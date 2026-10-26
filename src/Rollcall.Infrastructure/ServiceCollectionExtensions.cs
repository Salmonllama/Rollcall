// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the database context, migrator, and readiness probe.</summary>
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
}
