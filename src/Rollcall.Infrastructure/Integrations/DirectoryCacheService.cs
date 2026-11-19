// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Domain.Entities;
using Rollcall.Infrastructure.Persistence;

namespace Rollcall.Infrastructure.Integrations;

/// <summary>
/// Short-lived (5-minute TTL) Postgres directory cache for list views with manual refresh (M4).
/// </summary>
public sealed class DirectoryCacheService(RollcallDbContext db, TimeProvider clock)
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<T> GetOrFetchAsync<T>(
        string cacheKey,
        string kind,
        bool forceRefresh,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (!forceRefresh)
        {
            var cached = await db.DirectoryCache.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CacheKey == cacheKey, cancellationToken);
            if (cached is not null && now - cached.FetchedAt < DefaultTtl)
            {
                var deserialized = JsonSerializer.Deserialize<T>(cached.DataJson, JsonOptions);
                if (deserialized is not null)
                {
                    return deserialized;
                }
            }
        }

        var fresh = await factory(cancellationToken);
        var json = JsonSerializer.Serialize(fresh, JsonOptions);

        var existing = await db.DirectoryCache.FirstOrDefaultAsync(c => c.CacheKey == cacheKey, cancellationToken);
        if (existing is null)
        {
            db.DirectoryCache.Add(new DirectoryCacheEntity
            {
                CacheKey = cacheKey,
                Kind = kind,
                DataJson = json,
                FetchedAt = now,
            });
        }
        else
        {
            existing.DataJson = json;
            existing.FetchedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        return fresh;
    }

    public async Task InvalidateKindAsync(string kind, CancellationToken cancellationToken = default)
    {
        var rows = await db.DirectoryCache.Where(c => c.Kind == kind).ToListAsync(cancellationToken);
        if (rows.Count > 0)
        {
            db.DirectoryCache.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
