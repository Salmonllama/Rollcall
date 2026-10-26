// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Rollcall.Infrastructure.Persistence;
using Rollcall.TestSupport;

namespace Rollcall.Api.Tests;

public sealed class DatabaseMigratorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Concurrent_migrators_take_turns_and_both_succeed()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var first = PostgresFixture.CreateContext(connectionString);
        await using var second = PostgresFixture.CreateContext(connectionString);

        await Task.WhenAll(
            new DatabaseMigrator(first, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(),
            new DatabaseMigrator(second, NullLogger<DatabaseMigrator>.Instance).MigrateAsync());

        Assert.Empty(await first.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Migrating_an_up_to_date_database_changes_nothing()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = PostgresFixture.CreateContext(connectionString);
        var migrator = new DatabaseMigrator(db, NullLogger<DatabaseMigrator>.Instance);
        await migrator.MigrateAsync();

        await migrator.MigrateAsync();

        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
    }
}
