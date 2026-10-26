// SPDX-License-Identifier: GPL-3.0-or-later

using Rollcall.Api;
using Rollcall.Infrastructure;
using Rollcall.Infrastructure.Health;
using Rollcall.Infrastructure.Persistence;

// The container healthcheck runs "dotnet Rollcall.Api.dll healthcheck"; the image has no curl.
if (args is ["healthcheck", ..])
{
    return await HealthcheckCommand.RunAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddRollcallDatabase();

var app = builder.Build();

// Migrate before serving, so /healthz only answers once the schema is current. A failed
// migration throws here and the process exits non-zero, which the installer's update
// command detects and rolls back.
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.MapGet("/readyz", async (ReadinessProbe probe, CancellationToken cancellationToken) =>
{
    var report = await probe.CheckAsync(cancellationToken);
    var body = new { db = report.Db, migrations = report.Migrations, worker = report.Worker };
    return report.IsReady ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
});

await app.RunAsync();
return 0;

/// <summary>Entry point, exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
