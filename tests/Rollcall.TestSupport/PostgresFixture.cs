// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Npgsql;
using Rollcall.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rollcall.TestSupport;

/// <summary>
/// One Postgres 17 container per test assembly. Each test asks for its own empty database so
/// tests never see each other's rows.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17").Build();

    public async Task InitializeAsync() => await _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Creates an empty database and returns its connection string.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public static RollcallDbContext CreateContext(string connectionString) =>
        new(new DbContextOptionsBuilder<RollcallDbContext>().UseNpgsql(connectionString).Options);
}
