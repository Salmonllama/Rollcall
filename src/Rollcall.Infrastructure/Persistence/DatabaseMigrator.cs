// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Rollcall.Infrastructure.Persistence;

/// <summary>
/// Applies pending EF Core migrations while holding a Postgres advisory lock, so two app
/// containers starting at once never migrate concurrently.
/// </summary>
public sealed partial class DatabaseMigrator(RollcallDbContext db, ILogger<DatabaseMigrator> logger)
{
    // Arbitrary but fixed: every Rollcall process must use the same key.
    private const long AdvisoryLockKey = 0x526F6C6C63616C;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenWithRetryAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", connection))
        {
            lockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            // On an empty database EF logs a failed query against the missing history table at
            // Error level before creating it. Create it first so a fresh install logs no errors.
            await db.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken);

            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count == 0)
            {
                LogUpToDate(logger);
                return;
            }

            LogApplying(logger, pending.Count, pending);
            await db.Database.MigrateAsync(cancellationToken);
            LogApplied(logger);
        }
        finally
        {
            await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            unlockCommand.Parameters.AddWithValue("key", AdvisoryLockKey);
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>The database container can still be starting when the app starts; wait for it.</summary>
    private async Task<NpgsqlConnection> OpenWithRetryAsync(CancellationToken cancellationToken)
    {
        var connectionString = db.Database.GetConnectionString()
            ?? throw new InvalidOperationException("No database connection string is configured (ConnectionStrings__Rollcall).");
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            var connection = new NpgsqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (NpgsqlException ex) when (DateTimeOffset.UtcNow < deadline)
            {
                await connection.DisposeAsync();
                LogWaiting(logger, ex.Message);
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 5));
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Database schema is up to date.")]
    private static partial void LogUpToDate(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Applying {Count} database migration(s): {Migrations}.")]
    private static partial void LogApplying(ILogger logger, int count, IEnumerable<string> migrations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migrations applied.")]
    private static partial void LogApplied(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database not reachable yet, retrying: {Reason}")]
    private static partial void LogWaiting(ILogger logger, string reason);
}
