// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Rollcall.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef migrations add</c> build the context without a running host or database.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<RollcallDbContext>
{
    public RollcallDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RollcallDbContext>()
            .UseNpgsql("Host=localhost;Database=rollcall;Username=rollcall")
            .Options;
        return new RollcallDbContext(options);
    }
}
