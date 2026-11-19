// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;

namespace Rollcall.Infrastructure.Persistence;

public sealed class RollcallDbContext(DbContextOptions<RollcallDbContext> options) : DbContext(options)
{
    public DbSet<WorkerHeartbeat> WorkerHeartbeats => Set<WorkerHeartbeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkerHeartbeat>(entity =>
        {
            entity.ToTable("worker_heartbeat");
            entity.HasKey(h => h.WorkerId);
            entity.Property(h => h.WorkerId).HasColumnName("worker_id").HasMaxLength(200);
            entity.Property(h => h.BeatAt).HasColumnName("beat_at");
        });
    }
}
