// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rollcall.Contracts;
using Rollcall.Domain.Entities;
using Rollcall.Domain.Workflow;
using Rollcall.Infrastructure.Persistence;
using Rollcall.Infrastructure.Security;

namespace Rollcall.Infrastructure.Workflow;

/// <summary>
/// Append-only, hash-chained audit log service with filtered queries, streaming CSV export,
/// and chain integrity verification (sections 4.3, 4.4, M3, M10).
/// </summary>
public sealed class AuditService(RollcallDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim AppendLock = new(1, 1);

    public async Task<AuditEventEntity> AppendAsync(
        string actorUpn,
        string actorDisplayName,
        string action,
        string target,
        string outcome,
        object? details,
        Guid? runId = null,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        var rawJson = details is string s ? s : JsonSerializer.Serialize(details ?? new { }, JsonOptions);
        var sanitizedDetails = SecretRedactor.Redact(rawJson);
        var sanitizedSummary = summary is null ? null : SecretRedactor.Redact(summary);

        await AppendLock.WaitAsync(cancellationToken);
        try
        {
            var lastHash = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(e => e.Id)
                .Select(e => e.Hash)
                .FirstOrDefaultAsync(cancellationToken) ?? AuditHashChain.GenesisHash;

            // Truncate to microsecond precision to match PostgreSQL timestamptz storage identically.
            var utc = clock.GetUtcNow().ToUniversalTime();
            var pgUtc = new DateTimeOffset(utc.Ticks - (utc.Ticks % 10), TimeSpan.Zero);

            var entry = new AuditEventEntity
            {
                OccurredAt = pgUtc,
                ActorUpn = actorUpn,
                ActorDisplayName = actorDisplayName,
                Action = action,
                Target = target,
                DetailsJson = sanitizedDetails,
                Outcome = outcome,
                RunId = runId,
                Summary = sanitizedSummary,
                PrevHash = lastHash,
            };
            entry.Hash = AuditHashChain.ComputeHash(entry);

            db.AuditEvents.Add(entry);
            await db.SaveChangesAsync(cancellationToken);
            return entry;
        }
        finally
        {
            AppendLock.Release();
        }
    }

    public async Task<CursorPage<AuditEventDto>> QueryAsync(
        string? actor,
        string? action,
        string? target,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? restrictToActorUpn,
        long? cursorId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var query = BuildFilterQuery(actor, action, target, outcome, from, to, restrictToActorUpn);
        var total = await query.CountAsync(cancellationToken);

        if (cursorId.HasValue)
        {
            query = query.Where(e => e.Id < cursorId.Value);
        }

        var take = Math.Clamp(limit, 1, 200);
        var rows = await query
            .OrderByDescending(e => e.Id)
            .Take(take + 1)
            .ToListAsync(cancellationToken);

        string? nextCursor = null;
        if (rows.Count > take)
        {
            rows = rows.Take(take).ToList();
            nextCursor = rows[^1].Id.ToString(CultureInfo.InvariantCulture);
        }

        return new CursorPage<AuditEventDto>(
            rows.Select(ToDto).ToList(),
            nextCursor,
            total);
    }

    public async Task WriteCsvAsync(
        Stream destination,
        string? actor,
        string? action,
        string? target,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? restrictToActorUpn,
        CancellationToken cancellationToken = default)
    {
        // UTF-8 BOM for clean Excel opening (M10 criterion 2).
        await using var writer = new StreamWriter(destination, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), bufferSize: 16384, leaveOpen: true);
        await writer.WriteLineAsync("id,occurred_at,actor,actor_display_name,action,target,outcome,run_id,summary,hash");

        var query = BuildFilterQuery(actor, action, target, outcome, from, to, restrictToActorUpn)
            .OrderBy(e => e.Id);

        await foreach (var e in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var line = string.Join(
                ',',
                e.Id.ToString(CultureInfo.InvariantCulture),
                CsvEscape(e.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                CsvEscape(e.ActorUpn),
                CsvEscape(e.ActorDisplayName),
                CsvEscape(e.Action),
                CsvEscape(e.Target),
                CsvEscape(e.Outcome),
                CsvEscape(e.RunId?.ToString("D") ?? string.Empty),
                CsvEscape(e.Summary ?? string.Empty),
                CsvEscape(e.Hash));
            await writer.WriteLineAsync(line);
        }

        await writer.FlushAsync(cancellationToken);
    }

    public async Task<AuditVerificationResultDto> VerifyChainAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.AuditEvents.AsNoTracking()
            .OrderBy(e => e.Id)
            .ToListAsync(cancellationToken);
        return AuditHashChain.Verify(all);
    }

    public static AuditEventDto ToDto(AuditEventEntity e) => new(
        e.Id,
        e.OccurredAt,
        e.ActorUpn,
        e.ActorDisplayName,
        e.Action,
        e.Target,
        e.Outcome,
        e.RunId,
        e.Summary,
        e.DetailsJson,
        e.PrevHash,
        e.Hash);

    private IQueryable<AuditEventEntity> BuildFilterQuery(
        string? actor,
        string? action,
        string? target,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? restrictToActorUpn)
    {
        var query = db.AuditEvents.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(restrictToActorUpn))
        {
            var exact = restrictToActorUpn.Trim();
            query = query.Where(e => EF.Functions.ILike(e.ActorUpn, exact));
        }
        else if (!string.IsNullOrWhiteSpace(actor))
        {
            var pattern = $"%{actor.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.ActorUpn, pattern) || EF.Functions.ILike(e.ActorDisplayName, pattern));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            var pattern = $"%{action.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Action, pattern));
        }

        if (!string.IsNullOrWhiteSpace(target))
        {
            var pattern = $"%{target.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Target, pattern));
        }

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var exact = outcome.Trim();
            query = query.Where(e => EF.Functions.ILike(e.Outcome, exact));
        }

        if (from.HasValue)
        {
            query = query.Where(e => e.OccurredAt >= from.Value);
        }

        if (to.HasValue)
        {
            query = query.Where(e => e.OccurredAt <= to.Value);
        }

        return query;
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal))
        {
            return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }

        return value;
    }
}
